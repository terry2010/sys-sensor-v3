using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SystemMonitor.Service;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using StreamJsonRpc;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;

namespace SystemMonitor.Service.Services
{
    /// <summary>
    /// JSON-RPC 服务宿主，通过 Windows Named Pipe 对 UI 提供服务。
    /// 管道名称：\\.\pipe\sys_sensor_v3.rpc
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class RpcHostedService : BackgroundService
    {
        private const string PipeName = "sys_sensor_v3.rpc";
        private readonly ILogger<RpcHostedService> _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly HistoryStore _store;

        public RpcHostedService(ILogger<RpcHostedService> logger, HistoryStore store)
        {
            _logger = logger;
            _store = store;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false
            };
        }

        /// <summary>
        /// 后台循环：接受连接并发处理 JSON-RPC 会话（支持并发）。
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("RPC 服务启动，等待客户端连接…");
            // 初始化历史存储（SQLite）
            try { await _store.InitAsync(null, stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "HistoryStore 初始化失败"); }

            var backoffMs = 500;
            var sessions = new List<Task>();

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        var serverStream = CreateSecuredPipe();
                        await serverStream.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);

                        // 连接建立后，交由后台任务处理该会话；主循环立即继续监听下一连接
                        var task = HandleClientAsync(serverStream, stoppingToken);
                        sessions.Add(task);
                        // 清理已完成的任务，避免列表无限增长
                        sessions.RemoveAll(t => t.IsCompleted);
                        backoffMs = 500; // 有新连接，重置退避
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException ioex)
                    {
                        _logger.LogWarning(ioex, "RPC 会话 I/O 异常，将在 {Delay}ms 后重试", backoffMs);
                        await Task.Delay(backoffMs, stoppingToken).ConfigureAwait(false);
                        backoffMs = Math.Min(backoffMs * 2, 10_000);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "RPC 会话异常");
                        await Task.Delay(backoffMs, stoppingToken).ConfigureAwait(false);
                        backoffMs = Math.Min(backoffMs * 2, 10_000);
                    }
                }
            }
            finally
            {
                // 等待所有会话结束
                try { await Task.WhenAll(sessions).ConfigureAwait(false); } catch { }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream serverStream, CancellationToken stoppingToken)
        {
            using var _ = serverStream;
            _logger.LogInformation("客户端已连接");

            var reader = serverStream.UsePipeReader();
            var writer = serverStream.UsePipeWriter();
            var formatter = new SystemTextJsonFormatter();
            formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            var handler = new HeaderDelimitedMessageHandler(writer, reader, formatter);

            var rpcServer = new RpcServer(_logger, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _store);
            var rpc = new JsonRpc(handler, rpcServer);

            rpc.Disconnected += (s, e) =>
            {
                _logger.LogInformation("客户端断开：{Reason}", e?.Description);
            };

            rpc.StartListening();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var metricsTask = Task.Run(async () =>
            {
                var logEvery = GetMetricsLogEvery();
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (!rpcServer.MetricsPushEnabled)
                        {
                            await Task.Delay(300, cts.Token).ConfigureAwait(false);
                            continue;
                        }
                        var enabled = rpcServer.GetEnabledModules();
                        double? cpuVal = null;
                        (long total, long used)? memVal = null;
                        var payload = new Dictionary<string, object?>
                        {
                            ["ts"] = now,
                            ["seq"] = rpcServer.NextSeq(),
                        };
                        if (enabled.Contains("cpu"))
                        {
                            var cpu = GetCpuUsagePercent();
                            cpuVal = cpu;
                            payload["cpu"] = new { usage_percent = cpu };
                        }
                        if (enabled.Contains("memory"))
                        {
                            var mem = GetMemoryInfoMb();
                            memVal = (mem.total_mb, mem.used_mb);
                            payload["memory"] = new { total = mem.total_mb, used = mem.used_mb };
                        }
                        if (cpuVal.HasValue || memVal.HasValue)
                        {
                            rpcServer.AppendHistory(now, cpuVal, memVal);
                            // 持久化到 SQLite（忽略错误，不影响推送）
                            try { await rpcServer.PersistHistoryAsync(now, cpuVal, memVal).ConfigureAwait(false); } catch { }
                        }
                        await rpc.NotifyAsync("metrics", payload).ConfigureAwait(false);
                        var pushed = rpcServer.IncrementMetricsCount();
                        if (logEvery > 0 && pushed % logEvery == 0)
                        {
                            _logger.LogInformation("metrics 推送累计: {Count}", pushed);
                        }
                        var delay = rpcServer.GetCurrentIntervalMs(now);
                        await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "metrics 推送异常（忽略并继续）");
                        await Task.Delay(1000, cts.Token).ConfigureAwait(false);
                    }
                }
            }, cts.Token);

            await rpc.Completion.ConfigureAwait(false);
            cts.Cancel();
            try { await metricsTask.ConfigureAwait(false); } catch { }
        }

        private static int GetMetricsLogEvery()
        {
            var s = Environment.GetEnvironmentVariable("METRICS_LOG_EVERY");
            if (int.TryParse(s, out var n) && n > 0) return n;
            return 0;
        }

        /// <summary>
        /// 创建带 ACL 的 NamedPipeServerStream，限制为本机 SYSTEM/Administrators/当前用户。
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static NamedPipeServerStream CreateSecuredPipe()
        {
            // 构建自定义 ACL
            var pipeSecurity = new PipeSecurity();
            // SYSTEM
            pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            // Administrators
            pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            // 已通过身份验证的本地用户（允许前端普通用户连接）
            pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            try
            {
                // 显式添加“当前用户”FullControl，缓解某些环境下的 UAC/完整性级别导致的拒绝
                var current = WindowsIdentity.GetCurrent();
                if (current?.User != null)
                {
                    pipeSecurity.AddAccessRule(new PipeAccessRule(current.User, PipeAccessRights.FullControl, AccessControlType.Allow));
                }
            }
            catch { /* ignore */ }

            try
            {
                // 使用 Acl 扩展创建带 ACL 的命名管道
                // 允许并发连接：事件桥（长期占用）+ 前端短连接 RPC 调用
                // 注意：StreamJsonRpc 每个连接一个会话，服务端会在断开后继续接受后续连接
                var server = System.IO.Pipes.NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    pipeSecurity: pipeSecurity
                );
                return server;
            }
            catch (UnauthorizedAccessException)
            {
                // 回退：在极少数环境下，设置 ACL 会被拒绝。为保证可用性，退回到默认安全描述符。
                // 注意：此回退依赖系统默认 ACL，通常仅本用户可访问。
                return new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0
                );
            }
        }

        /// <summary>
        /// 实际提供 JSON-RPC 方法的目标类。
        /// </summary>
        private sealed class RpcServer
        {
            private readonly ILogger _logger;
            private long _seq;
            private readonly object _lock = new();
            private int _baseIntervalMs = 1000;
            private int? _burstIntervalMs;
            private long _burstExpiresAt;
            private long _metricsPushed;
            private readonly Dictionary<string, int> _moduleIntervals = new(StringComparer.OrdinalIgnoreCase);
            private bool _metricsEnabled = false; // 需订阅后才开始推送
            // 简易内存历史缓冲区（环形，最多保留最近 MaxHistory 条）
            private readonly List<HistoryItem> _history = new();
            private const int MaxHistory = 10_000;
            private static readonly HashSet<string> _supportedCapabilities = new(new[] { "metrics_stream", "burst_mode", "history_query" }, StringComparer.OrdinalIgnoreCase);
            private readonly HistoryStore _store;

            public RpcServer(ILogger logger, long initialSeq, HistoryStore store)
            {
                _logger = logger;
                _seq = initialSeq;
                _store = store;
            }

            public long NextSeq() => Interlocked.Increment(ref _seq);

            public long IncrementMetricsCount() => Interlocked.Increment(ref _metricsPushed);

            public bool MetricsPushEnabled
            {
                get { lock (_lock) { return _metricsEnabled; } }
            }

            public int GetCurrentIntervalMs(long now)
            {
                lock (_lock)
                {
                    if (_burstIntervalMs.HasValue && now < _burstExpiresAt)
                    {
                        return Math.Max(50, _burstIntervalMs.Value);
                    }
                    var interval = _baseIntervalMs;
                    if (_moduleIntervals.Count > 0)
                    {
                        var minMod = _moduleIntervals.Values.Min();
                        interval = Math.Min(interval, minMod);
                    }
                    return interval;
                }
            }

            public ISet<string> GetEnabledModules()
            {
                lock (_lock)
                {
                    if (_moduleIntervals.Count == 0)
                    {
                        return new HashSet<string>(new[] { "cpu", "memory" }, StringComparer.OrdinalIgnoreCase);
                    }
                    return new HashSet<string>(_moduleIntervals.Keys, StringComparer.OrdinalIgnoreCase);
                }
            }

            private sealed class HistoryItem
            {
                public long ts { get; set; }
                public double? cpu { get; set; }
                public long? mem_total { get; set; }
                public long? mem_used { get; set; }
            }

            public void AppendHistory(long ts, double? cpu, (long total, long used)? mem)
            {
                lock (_lock)
                {
                    _history.Add(new HistoryItem
                    {
                        ts = ts,
                        cpu = cpu,
                        mem_total = mem?.total,
                        mem_used = mem?.used
                    });
                    // 限制历史长度
                    if (_history.Count > MaxHistory)
                    {
                        var remove = _history.Count - MaxHistory;
                        _history.RemoveRange(0, remove);
                    }
                }
            }

            public Task PersistHistoryAsync(long ts, double? cpu, (long total, long used)? mem)
                => _store.AppendAsync(ts, cpu, mem);

            /// <summary>
            /// 握手认证：校验 token（MVP 先放行非空），返回会话信息。
            /// </summary>
            public Task<object> hello(HelloParams p)
            {
                if (p == null)
                {
                    throw new ArgumentNullException(nameof(p));
                }
                if (string.IsNullOrWhiteSpace(p.token))
                {
                    // 认证失败：未携带 token
                    throw new UnauthorizedAccessException("unauthorized");
                }
                if (p.protocol_version != 1)
                {
                    // 协议不支持
                    _logger.LogWarning("hello validation failed: unsupported protocol_version={Proto}", p.protocol_version);
                    throw new InvalidOperationException($"not_supported: protocol_version={p.protocol_version}");
                }
                if (p.capabilities != null && p.capabilities.Length > 0)
                {
                    var unsupported = p.capabilities.Where(c => !_supportedCapabilities.Contains(c)).ToArray();
                    if (unsupported.Length > 0)
                    {
                        _logger.LogWarning("hello validation failed: unsupported capabilities={Caps}", string.Join(',', unsupported));
                        throw new InvalidOperationException($"not_supported: capabilities=[{string.Join(',', unsupported)}]");
                    }
                }

                var sessionId = Guid.NewGuid().ToString();
                var result = new
                {
                    server_version = "1.0.0",
                    protocol_version = 1,
                    capabilities = _supportedCapabilities.ToArray(),
                    session_id = sessionId
                };
                _logger.LogInformation("hello ok: app={App} proto={Proto} caps=[{Caps}] session_id={SessionId}", p.app_version, p.protocol_version, p.capabilities == null ? string.Empty : string.Join(',', p.capabilities), sessionId);
                return Task.FromResult<object>(result);
            }

            /// <summary>
            /// 获取即时快照（最小实现：CPU/内存信息）。
            /// </summary>
            public Task<object> snapshot(SnapshotParams? p)
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var mem = GetMemoryInfoMb();
                var cpu = GetCpuUsagePercent();
                var result = new
                {
                    ts,
                    cpu = new { usage_percent = cpu },
                    memory = new { total = mem.total_mb, used = mem.used_mb },
                    disk = new { read_bytes_per_sec = 1024, write_bytes_per_sec = 2048 }
                };
                _logger.LogInformation("snapshot called, modules={Modules}", p?.modules == null ? "*" : string.Join(',', p.modules));
                return Task.FromResult<object>(result);
            }

            /// <summary>
            /// 临时高频订阅：在 ttl_ms 内将推流间隔降为 interval_ms。
            /// </summary>
            public Task<object> burst_subscribe(BurstParams p)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (p == null || p.interval_ms <= 0 || p.ttl_ms <= 0)
                {
                    throw new ArgumentException("invalid burst params");
                }
                lock (_lock)
                {
                    _burstIntervalMs = p.interval_ms;
                    _burstExpiresAt = now + p.ttl_ms;
                }
                _logger.LogInformation("burst_subscribe: interval={Interval}ms ttl={Ttl}ms -> expires_at={Expires}", p.interval_ms, p.ttl_ms, _burstExpiresAt);
                return Task.FromResult<object>(new { ok = true, expires_at = _burstExpiresAt });
            }

            /// <summary>
            /// 开启/关闭 metrics 推送（默认关闭，避免与短连接响应混流）。
            /// </summary>
            public Task<object> subscribe_metrics(SubscribeMetricsParams p)
            {
                lock (_lock)
                {
                    _metricsEnabled = p != null && p.enable;
                }
                _logger.LogInformation("subscribe_metrics: enable={Enable}", _metricsEnabled);
                return Task.FromResult<object>(new { ok = true, enabled = _metricsEnabled });
            }

            /// <summary>
            /// 设置配置（占位）。
            /// </summary>
            public Task<object> set_config(SetConfigParams p)
            {
                if (p == null)
                {
                    throw new ArgumentNullException(nameof(p));
                }
                _logger.LogInformation("set_config 收到: base_interval_ms={Base}, module_intervals=[{Mods}]",
                    p.base_interval_ms,
                    p.module_intervals == null ? "" : string.Join(", ", p.module_intervals.Select(kv => $"{kv.Key}={kv.Value}")));

                int? newBase = null;
                if (p.base_interval_ms.HasValue)
                {
                    var v = p.base_interval_ms.Value;
                    if (v <= 0)
                    {
                        throw new ArgumentException("base_interval_ms must be positive");
                    }
                    // 基础保护：至少 100ms，避免过低导致 CPU 抢占
                    newBase = Math.Max(100, v);
                }

                if (newBase.HasValue)
                {
                    lock (_lock)
                    {
                        _baseIntervalMs = newBase.Value;
                    }
                    _logger.LogInformation("set_config: base_interval_ms -> {Base}ms", newBase.Value);
                }

                if (p.module_intervals != null && p.module_intervals.Count > 0)
                {
                    var sanitized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in p.module_intervals)
                    {
                        var name = (kv.Key ?? string.Empty).Trim();
                        var val = kv.Value;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (val <= 0) throw new ArgumentException($"module_intervals[{name}] must be positive");
                        sanitized[name] = Math.Max(100, val);
                    }
                    lock (_lock)
                    {
                        _moduleIntervals.Clear();
                        foreach (var kv in sanitized)
                        {
                            _moduleIntervals[kv.Key] = kv.Value;
                        }
                    }
                    _logger.LogInformation("set_config: module_intervals -> {Intervals}", string.Join(", ", sanitized.Select(kv => $"{kv.Key}={kv.Value}ms")));
                }

                var result = new
                {
                    ok = true,
                    base_interval_ms = _baseIntervalMs,
                    effective_intervals = new Dictionary<string, int>(_moduleIntervals, StringComparer.OrdinalIgnoreCase)
                };
                return Task.FromResult<object>(result);
            }

            /// <summary>
            /// 启动采集（占位）。
            /// </summary>
            public Task<object> start(StartParams? p)
            {
                return Task.FromResult<object>(new { ok = true, started_modules = p?.modules ?? Array.Empty<string>() });
            }

            /// <summary>
            /// 停止采集（占位）。
            /// </summary>
            public Task<object> stop()
            {
                return Task.FromResult<object>(new { ok = true });
            }

            /// <summary>
            /// 历史查询：从内存历史缓冲区返回真实数据，支持 step_ms 聚合（按时间桶选用最后一条）。
            /// </summary>
            public async Task<object> query_history(QueryHistoryParams p)
            {
                if (p == null) throw new ArgumentNullException(nameof(p));
                var from = p.from_ts;
                var to = p.to_ts <= 0 || p.to_ts < from ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : p.to_ts;
                var want = new HashSet<string>(p.modules ?? new[] { "cpu", "memory" }, StringComparer.OrdinalIgnoreCase);
                object[] resultItems;
                // 1) 先尝试从 SQLite 读取
                var rows = await _store.QueryAsync(from, to).ConfigureAwait(false);
                if (rows.Count > 0)
                {
                    if (p.step_ms.HasValue && p.step_ms.Value > 0)
                    {
                        var step = p.step_ms.Value;
                        resultItems = rows
                            .GroupBy(r => (r.Ts - from) / step)
                            .OrderBy(g => g.Key)
                            .Select(g =>
                            {
                                var last = g.Last();
                                return new
                                {
                                    ts = from + (g.Key + 1) * step,
                                    cpu = want.Contains("cpu") && last.Cpu.HasValue ? new { usage_percent = last.Cpu.Value } : null,
                                    memory = want.Contains("memory") && last.MemTotal.HasValue ? new { total = last.MemTotal.Value, used = last.MemUsed!.Value } : null
                                } as object;
                            })
                            .ToArray();
                    }
                    else
                    {
                        resultItems = rows
                            .Select(r => (object)new
                            {
                                ts = r.Ts,
                                cpu = want.Contains("cpu") && r.Cpu.HasValue ? new { usage_percent = r.Cpu.Value } : null,
                                memory = want.Contains("memory") && r.MemTotal.HasValue ? new { total = r.MemTotal.Value, used = r.MemUsed!.Value } : null
                            })
                            .ToArray();
                    }
                }
                else
                {
                    // 2) 回退到内存窗口
                    List<HistoryItem> slice;
                    lock (_lock)
                    {
                        slice = _history.Where(h => h.ts >= from && h.ts <= to).ToList();
                    }
                    if (p.step_ms.HasValue && p.step_ms.Value > 0)
                    {
                        var step = p.step_ms.Value;
                        resultItems = slice
                            .GroupBy(h => (h.ts - from) / step)
                            .OrderBy(g => g.Key)
                            .Select(g =>
                            {
                                var last = g.Last();
                                return new
                                {
                                    ts = from + (g.Key + 1) * step,
                                    cpu = want.Contains("cpu") && last.cpu.HasValue ? new { usage_percent = last.cpu.Value } : null,
                                    memory = want.Contains("memory") && last.mem_total.HasValue ? new { total = last.mem_total.Value, used = last.mem_used!.Value } : null
                                } as object;
                            })
                            .ToArray();
                    }
                    else
                    {
                        resultItems = slice
                            .Select(h => (object)new
                            {
                                ts = h.ts,
                                cpu = want.Contains("cpu") && h.cpu.HasValue ? new { usage_percent = h.cpu.Value } : null,
                                memory = want.Contains("memory") && h.mem_total.HasValue ? new { total = h.mem_total.Value, used = h.mem_used!.Value } : null
                            })
                            .ToArray();
                    }
                }
                // 回退：当窗口内没有历史数据时，返回一条当前即时值，避免空结果
                if (resultItems.Length == 0)
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var cpu = GetCpuUsagePercent();
                    var mem = GetMemoryInfoMb();
                    var item = new
                    {
                        ts = now,
                        cpu = want.Contains("cpu") ? new { usage_percent = cpu } : null,
                        memory = want.Contains("memory") ? new { total = mem.total_mb, used = mem.used_mb } : null
                    } as object;
                    resultItems = new[] { item };
                }
                return new { ok = true, items = resultItems };
            }
        }

        #region DTOs
        /// <summary>
        /// hello 参数（字段名遵循 snake_case）。
        /// </summary>
        public sealed class HelloParams
        {
            public string app_version { get; set; } = string.Empty;
            public int protocol_version { get; set; }
            public string token { get; set; } = string.Empty;
            public string[]? capabilities { get; set; }
        }

        public sealed class SnapshotParams
        {
            public string[]? modules { get; set; }
        }

        public sealed class BurstParams
        {
            public string[]? modules { get; set; }
            public int interval_ms { get; set; }
            public int ttl_ms { get; set; }
        }

        public sealed class SetConfigParams
        {
            public int? base_interval_ms { get; set; }
            public Dictionary<string, int>? module_intervals { get; set; }
            public bool? persist { get; set; }
        }

        public sealed class StartParams
        {
            public string[]? modules { get; set; }
        }

        public sealed class QueryHistoryParams
        {
            public long from_ts { get; set; }
            public long to_ts { get; set; }
            public string[]? modules { get; set; }
            public int? step_ms { get; set; }
        }
        
        public sealed class SubscribeMetricsParams
        {
            public bool enable { get; set; }
        }
        #endregion

        // Helpers
        private static (long total_mb, long used_mb) GetMemoryInfoMb()
        {
            try
            {
                if (TryGetMemoryStatus(out var status))
                {
                    long totalMb = (long)(status.ullTotalPhys / (1024 * 1024));
                    long availMb = (long)(status.ullAvailPhys / (1024 * 1024));
                    long usedMb = Math.Max(0, totalMb - availMb);
                    return (totalMb, usedMb);
                }
            }
            catch
            {
                // ignore
            }
            // 回退到固定值，保证接口不失败
            return (16_000, 8_000);
        }

        // CPU usage via GetSystemTimes
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        private static readonly object _cpuLock = new();
        private static bool _cpuInit;
        private static ulong _prevIdle, _prevKernel, _prevUser;

        private static double GetCpuUsagePercent()
        {
            try
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user))
                {
                    return 0.0;
                }
                static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
                var idleNow = ToUInt64(idle);
                var kernelNow = ToUInt64(kernel);
                var userNow = ToUInt64(user);
                double usage = 0.0;
                lock (_cpuLock)
                {
                    if (_cpuInit)
                    {
                        var idleDelta = idleNow - _prevIdle;
                        var kernelDelta = kernelNow - _prevKernel;
                        var userDelta = userNow - _prevUser;
                        // kernel 包含 idle，需要与 user 一起作为 total，再减去 idle 得到 busy
                        var total = kernelDelta + userDelta;
                        var busy = total > idleDelta ? (total - idleDelta) : 0UL;
                        if (total > 0)
                        {
                            usage = Math.Clamp(100.0 * busy / total, 0.0, 100.0);
                        }
                    }
                    _prevIdle = idleNow;
                    _prevKernel = kernelNow;
                    _prevUser = userNow;
                    _cpuInit = true;
                }
                return usage;
            }
            catch
            {
                return 0.0;
            }
        }

        // Memory via GlobalMemoryStatusEx
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        private static bool TryGetMemoryStatus(out MEMORYSTATUSEX status)
        {
            status = new MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            return GlobalMemoryStatusEx(ref status);
        }
    }
}
