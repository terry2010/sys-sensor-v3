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

        public RpcHostedService(ILogger<RpcHostedService> logger)
        {
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false
            };
        }

        /// <summary>
        /// 后台循环：接受连接并处理 JSON-RPC 会话。
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("RPC 服务启动，等待客户端连接…");

            var backoffMs = 500;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var serverStream = CreateSecuredPipe();
                    await serverStream.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);

                    _logger.LogInformation("客户端已连接");

                    // HeaderDelimited 分帧（PipeReader/PipeWriter + SystemTextJsonFormatter）
                    var reader = serverStream.UsePipeReader();
                    var writer = serverStream.UsePipeWriter();
                    var formatter = new SystemTextJsonFormatter();
                    // 设置命名策略（snake_case）
                    formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                    var handler = new HeaderDelimitedMessageHandler(writer, reader, formatter);

                    var rpcServer = new RpcServer(_logger, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    var rpc = new JsonRpc(handler, rpcServer);

                    rpc.Disconnected += (s, e) =>
                    {
                        _logger.LogInformation("客户端断开：{Reason}", e?.Description);
                    };

                    rpc.StartListening();

                    // 指标推流任务（按当前间隔，支持 burst_subscribe）
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                    {
                        var metricsTask = Task.Run(async () =>
                        {
                            var logEvery = GetMetricsLogEvery();
                            while (!cts.Token.IsCancellationRequested)
                            {
                                try
                                {
                                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                    // 动态构造仅包含启用模块的 payload
                                    var enabled = rpcServer.GetEnabledModules();
                                    var payload = new Dictionary<string, object?>
                                    {
                                        ["ts"] = now,
                                        ["seq"] = rpcServer.NextSeq(),
                                    };
                                    if (enabled.Contains("cpu"))
                                    {
                                        var cpu = GetCpuUsagePercent();
                                        payload["cpu"] = new { usage_percent = cpu };
                                    }
                                    if (enabled.Contains("memory"))
                                    {
                                        var mem = GetMemoryInfoMb();
                                        payload["memory"] = new { total = mem.total_mb, used = mem.used_mb };
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

                        // 等待到断开
                        await rpc.Completion.ConfigureAwait(false);
                        cts.Cancel();
                        try { await metricsTask.ConfigureAwait(false); } catch { /* ignore */ }
                    }

                    // 正常断开后重置退避时间
                    backoffMs = 500;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ioex)
                {
                    // 常见：ERROR_PIPE_BUSY（所有管道实例都在使用中）
                    _logger.LogWarning(ioex, "RPC 会话 I/O 异常，将在 {Delay}ms 后重试", backoffMs);
                    await Task.Delay(backoffMs, stoppingToken).ConfigureAwait(false);
                    backoffMs = Math.Min(backoffMs * 2, 10_000); // 指数退避上限 10s
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "RPC 会话异常");
                    await Task.Delay(backoffMs, stoppingToken).ConfigureAwait(false);
                    backoffMs = Math.Min(backoffMs * 2, 10_000);
                }
            }

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
            var pipeSecurity = new PipeSecurity();

            // SYSTEM
            pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            // Administrators
            pipeSecurity.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            // 当前交互用户
            pipeSecurity.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
                PipeAccessRights.ReadWrite, AccessControlType.Allow));

            // 使用 Acl 扩展创建带 ACL 的命名管道
            var server = System.IO.Pipes.NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                transmissionMode: PipeTransmissionMode.Byte,
                options: PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity: pipeSecurity
            );

            return server;
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
            private static readonly HashSet<string> _supportedCapabilities = new(new[] { "metrics_stream", "burst_mode", "history_query" }, StringComparer.OrdinalIgnoreCase);

            public RpcServer(ILogger logger, long initialSeq)
            {
                _logger = logger;
                _seq = initialSeq;
            }

            public long NextSeq() => Interlocked.Increment(ref _seq);

            public long IncrementMetricsCount() => Interlocked.Increment(ref _metricsPushed);

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
            /// 历史查询（最小桩实现）：返回固定/Mock 数据。
            /// </summary>
            public Task<object> query_history(QueryHistoryParams p)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var items = new object[]
                {
                    new { ts = now - 3000, cpu = new { usage_percent = 10.5 }, memory = new { total = 16_000, used = 7_600 } },
                    new { ts = now - 2000, cpu = new { usage_percent = 11.0 }, memory = new { total = 16_000, used = 7_650 } },
                    new { ts = now - 1000, cpu = new { usage_percent = 12.0 }, memory = new { total = 16_000, used = 7_700 } },
                };
                return Task.FromResult<object>(new { ok = true, items });
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
