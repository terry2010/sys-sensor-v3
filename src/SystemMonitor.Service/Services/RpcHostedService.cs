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
using System.Diagnostics;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using static SystemMonitor.Service.Services.SystemInfo;
using SystemMonitor.Service.Services.Collectors;

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

        // CPU breakdown 已迁移到 Helpers/SystemInfo.cs（GetCpuBreakdownPercent）

        private async Task HandleClientAsync(NamedPipeServerStream serverStream, CancellationToken stoppingToken)
        {
            using var streamLease = serverStream;
            _logger.LogInformation("客户端已连接");

            var reader = serverStream.UsePipeReader();
            var writer = serverStream.UsePipeWriter();
            var formatter = new SystemTextJsonFormatter();
            formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            var handler = new HeaderDelimitedMessageHandler(writer, reader, formatter);

            var connId = Guid.NewGuid();
            _logger.LogInformation("客户端会话建立: conn={ConnId}", connId);
            var rpcServer = new RpcServer(_logger, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _store, connId);
            var rpc = new JsonRpc(handler, rpcServer);
            rpcServer.SetJsonRpc(rpc);

            rpc.Disconnected += (s, e) =>
            {
                _logger.LogInformation("客户端断开：{Reason} conn={ConnId}", e?.Description, connId);
                try
                {
                    var reason = string.IsNullOrWhiteSpace(e?.Description) ? "disconnected" : e!.Description!;
                    // 统一上报断线事件
                    var payload = new { reason };
                    rpcServer.NotifyBridge("bridge_disconnected", payload);
                }
                catch { /* ignore */ }
            };

            rpc.StartListening();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var metricsTask = Task.Run(async () =>
            {
                var logEvery = GetMetricsLogEvery();
                var consecutiveErrors = 0;
                // 故障注入计数（仅当前会话内生效）
                int simCount = 0; bool simTriggered = false;
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        // 推送条件：必须是“事件桥接”连接，且开启了 metrics 订阅
                        if (!rpcServer.IsBridgeConnection || !rpcServer.MetricsPushEnabled)
                        {
                            await Task.Delay(300, cts.Token).ConfigureAwait(false);
                            continue;
                        }
                        // 在短期抑制窗口内，避免与当前 RPC 响应交叉
                        if (rpcServer.IsPushSuppressed(now))
                        {
                            await Task.Delay(50, cts.Token).ConfigureAwait(false);
                            continue;
                        }
                        var enabled = rpcServer.GetEnabledModules();
                        double? cpuVal = null;
                        (long total, long used)? memVal = null;
                        // 先准备 seq 值，再放入 payload
                        var payload = new Dictionary<string, object?>
                        {
                            ["ts"] = now,
                            ["seq"] = rpcServer.NextSeq(),
                        };
                        // 通过采集器抽象生成各模块字段
                        foreach (var c in MetricsRegistry.Collectors)
                        {
                            if (!enabled.Contains(c.Name)) continue;
                            try
                            {
                                var val = c.Collect();
                                if (val != null) payload[c.Name] = val;
                            }
                            catch { /* ignore collector error */ }
                        }
                        // 维持历史/持久化所需的 CPU/Memory 数值（避免从匿名对象中反射）
                        if (enabled.Contains("cpu"))
                        {
                            try { var cpu = GetCpuUsagePercent(); cpuVal = cpu; }
                            catch { /* ignore cpu read error */ }
                        }
                        if (enabled.Contains("memory"))
                        {
                            try { var mem = GetMemoryInfoMb(); memVal = (mem.total_mb, mem.used_mb); }
                            catch { /* ignore mem read error */ }
                        }
                        if (cpuVal.HasValue || memVal.HasValue)
                        {
                            rpcServer.AppendHistory(now, cpuVal, memVal);
                            // 持久化到 SQLite（改为异步，不阻塞推送循环；忽略错误）
                            _ = Task.Run(async () => { try { await rpcServer.PersistHistoryAsync(now, cpuVal, memVal).ConfigureAwait(false); } catch { } });
                        }
                        // 故障注入：当设置 SIM_METRICS_ERROR 时，在达到阈值的第 N 次推送前抛出一次异常
                        var sim = Environment.GetEnvironmentVariable("SIM_METRICS_ERROR");
                        if (!string.IsNullOrEmpty(sim))
                        {
                            int threshold = 3;
                            if (int.TryParse(sim, out var n) && n > 0) threshold = n;
                            simCount++;
                            if (!simTriggered && simCount >= threshold)
                            {
                                simTriggered = true;
                                throw new Exception("simulated metrics push error");
                            }
                        }
                        await rpc.NotifyAsync("metrics", payload).ConfigureAwait(false);
                        var pushed = rpcServer.IncrementMetricsCount();
                        if (logEvery > 0 && pushed % logEvery == 0)
                        {
                            _logger.LogInformation("metrics 推送累计: {Count}", pushed);
                        }
                        var delay = rpcServer.GetCurrentIntervalMs(now);
                        await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                        consecutiveErrors = 0; // 只要成功一次就清零
                    }
                    catch (OperationCanceledException)
                    {
                        // 推送循环被取消，一般是服务器停止或会话结束
                        try { rpcServer.NotifyBridge("bridge_disconnected", new { reason = "operation_canceled" }); } catch { }
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "metrics 推送异常（忽略并继续）");
                        // 尝试上报 bridge_error，带原因
                        try { rpcServer.NotifyBridge("bridge_error", new { reason = "metrics_push_exception", message = ex.Message }); } catch { }
                        consecutiveErrors++;
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
        #if false
        private sealed class RpcServer
        {
            private readonly ILogger _logger;
            private long _seq;
            private readonly object _lock = new();
            private readonly Guid _connId;
            // 标记该连接是否为“事件桥”：默认 false；仅当 hello(capabilities 含 metrics_stream) 后才视为桥接
            private bool _isBridge = false;
            // 全局订阅开关（跨会话共享），确保任意连接的 subscribe_metrics 立即影响事件桥推流
            private static readonly object _subLock = new();
            private static bool _s_metricsEnabled = false;
            private int _baseIntervalMs = 1000;
            private int? _burstIntervalMs;
            private long _burstExpiresAt;
            private long _metricsPushed;
            private readonly Dictionary<string, int> _moduleIntervals = new(StringComparer.OrdinalIgnoreCase);
            // 移除实例级 _metricsEnabled，改为使用全局 _s_metricsEnabled
            // 简易内存历史缓冲区（环形，最多保留最近 MaxHistory 条）
            private readonly List<HistoryItem> _history = new();
            private const int MaxHistory = 10_000;
            private static readonly HashSet<string> _supportedCapabilities = new(new[] { "metrics_stream", "burst_mode", "history_query" }, StringComparer.OrdinalIgnoreCase);
            private readonly HistoryStore _store;
        private JsonRpc? _rpc;

            public RpcServer(ILogger logger, long initialSeq, HistoryStore store, Guid connId)
            {
                _logger = logger;
                _seq = initialSeq;
                _store = store;
                _connId = connId;
            }

            public long NextSeq() => Interlocked.Increment(ref _seq);

            public long IncrementMetricsCount() => Interlocked.Increment(ref _metricsPushed);
            
            public void SetJsonRpc(JsonRpc rpc)
            {
                _rpc = rpc;
            }

            // 发送桥接层事件（如 bridge_error/bridge_disconnected）。
            // 注意：若连接已断开，通知可能无法送达。
            internal void NotifyBridge(string @event, object payload)
            {
                try
                {
                    _ = _rpc?.NotifyAsync(@event, payload);
                }
                catch { /* 忽略通知失败 */ }
            }

            // 发送最小版 state 事件（通知）。字段：ts, phase, 可选 reason/extra。
            private void EmitState(string phase, string? reason = null, object? extra = null)
            {
                try
                {
                    var payload = new
                    {
                        ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        phase,
                        reason,
                        extra
                    };
                    _ = _rpc?.NotifyAsync("state", payload);
                    _logger.LogInformation("state emitted: phase={Phase} reason={Reason}", phase, reason);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "emit state failed (ignored)");
                }
            }

            public bool MetricsPushEnabled
            {
                get { lock (_subLock) { return _s_metricsEnabled; } }
            }

            public bool IsBridgeConnection
            {
                get { lock (_lock) { return _isBridge; } }
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
                    throw new InvalidOperationException("invalid_params: missing body");
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
                // 若声明支持 metrics_stream，则将该连接标记为事件桥
                if (p.capabilities != null && p.capabilities.Any(c => string.Equals(c, "metrics_stream", StringComparison.OrdinalIgnoreCase)))
                {
                    lock (_lock) { _isBridge = true; }
                    // 为稳妥起见：桥接握手成功即默认开启推送（即使订阅指令尚未来得及发出）
                    lock (_subLock) { _s_metricsEnabled = true; }
                    // 连接不再加入全局连接表，metrics 仅由本会话的推送循环负责
                    _logger.LogInformation("hello ok (bridge): app={App} proto={Proto} caps=[{Caps}] session_id={SessionId} conn={ConnId}", p.app_version, p.protocol_version, p.capabilities == null ? string.Empty : string.Join(',', p.capabilities), sessionId, _connId);
                    
                    // 桥接连接建立后自动启动采集（默认采集CPU和内存）
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // 延迟500毫秒，确保桥接连接完全建立
                            await Task.Delay(500);
                            await start(new StartParams { modules = new[] { "cpu", "mem" } });
                            _logger.LogInformation("自动启动采集模块成功");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "自动启动采集模块失败");
                        }
                    });
                }
                else
                {
                    _logger.LogInformation("hello ok: app={App} proto={Proto} caps=[{Caps}] session_id={SessionId} conn={ConnId}", p.app_version, p.protocol_version, p.capabilities == null ? string.Empty : string.Join(',', p.capabilities), sessionId, _connId);
                }
                return Task.FromResult<object>(result);
            }

            /// <summary>
            /// 获取即时快照（最小实现：CPU/内存信息）。
            /// </summary>
            public Task<object> snapshot(SnapshotParams? p)
            {
                var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                // 规范化模块名（mem -> memory），未指定则默认 cpu/memory
                HashSet<string> want;
                if (p?.modules != null && p.modules.Length > 0)
                {
                    want = new HashSet<string>(p.modules.Select(m => (m ?? string.Empty).Trim().ToLowerInvariant() == "mem" ? "memory" : (m ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    want = new HashSet<string>(new[] { "cpu", "memory" }, StringComparer.OrdinalIgnoreCase);
                }
                var payload = new Dictionary<string, object?> { ["ts"] = ts };
                foreach (var c in MetricsRegistry.Collectors)
                {
                    if (!want.Contains(c.Name)) continue;
                    var val = c.Collect();
                    if (val != null) payload[c.Name] = val;
                }
                _logger.LogInformation("snapshot called, modules={Modules}", p?.modules == null ? "*" : string.Join(',', p.modules));
                return Task.FromResult<object>(payload);
            }

            /// <summary>
            /// 临时高频订阅：在 ttl_ms 内将推流间隔降为 interval_ms。
            /// </summary>
            public Task<object> burst_subscribe(BurstParams p)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (p == null || p.interval_ms <= 0 || p.ttl_ms <= 0)
                {
                    throw new InvalidOperationException("invalid_params: interval_ms>0 && ttl_ms>0 required");
                }
                lock (_lock)
                {
                    _burstIntervalMs = p.interval_ms;
                    _burstExpiresAt = now + p.ttl_ms;
                }
                _logger.LogInformation("burst_subscribe: interval={Interval}ms ttl={Ttl}ms -> expires_at={Expires}", p.interval_ms, p.ttl_ms, _burstExpiresAt);
                // 发出状态事件：burst
                EmitState("burst", null, new { interval_ms = p.interval_ms, ttl_ms = p.ttl_ms, expires_at = _burstExpiresAt });
                return Task.FromResult<object>(new { ok = true, expires_at = _burstExpiresAt });
            }

            /// <summary>
            /// 开启/关闭 metrics 推送（默认关闭，避免与短连接响应混流）。
            /// </summary>
            public Task<object> subscribe_metrics(SubscribeMetricsParams p)
            {
                bool enabled;
                lock (_subLock)
                {
                    _s_metricsEnabled = p != null && p.enable;
                    enabled = _s_metricsEnabled;
                }
                _logger.LogInformation("subscribe_metrics: enable={Enable} conn={ConnId}", enabled, _connId);
                return Task.FromResult<object>(new { ok = true, enabled });
            }

            /// <summary>
            /// 设置配置（占位）。
            /// </summary>
            public Task<object> set_config(SetConfigParams p)
            {
                if (p == null)
                {
                    throw new InvalidOperationException("invalid_params: missing body");
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
                        throw new InvalidOperationException("invalid_params: base_interval_ms must be positive");
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
                        if (val <= 0) throw new InvalidOperationException($"invalid_params: module_intervals[{name}] must be positive");
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
            /// 启动采集模块。
            /// </summary>
            public Task<object> start(StartParams? p)
            {
                var modules = p?.modules ?? new[] { "cpu", "mem" };
                // 将外部传入的模块名规范化到内部命名（mem -> memory）并写入实例模块配置
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in modules)
                {
                    if (string.IsNullOrWhiteSpace(m)) continue;
                    var name = m.Trim().ToLowerInvariant() == "mem" ? "memory" : m.Trim();
                    map[name] = Math.Max(100, _baseIntervalMs);
                }
                lock (_lock)
                {
                    _moduleIntervals.Clear();
                    foreach (var kv in map)
                    {
                        _moduleIntervals[kv.Key] = kv.Value;
                    }
                }
                _logger.LogInformation("start called, modules={modules}", string.Join(",", modules));
                // 发出状态事件：start
                EmitState("start", null, new { modules });
                return Task.FromResult<object>(new { ok = true, started_modules = modules });
            }

            /// <summary>
            /// 停止采集模块。
            /// </summary>
            public Task<object> stop()
            {
                // 清空模块设置，推送循环将依据空集合回退到默认模块或停止
                lock (_lock)
                {
                    _moduleIntervals.Clear();
                }
                _logger.LogInformation("stop called, metrics collection stopped");
                // 发出状态事件：stop
                EmitState("stop");
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
                // 1) 先尝试从 SQLite 读取（支持聚合表）
                var useAgg = !string.IsNullOrWhiteSpace(p.agg) && !string.Equals(p.agg, "raw", StringComparison.OrdinalIgnoreCase);
                var rows = useAgg ? await _store.QueryAggAsync(p.agg!, from, to).ConfigureAwait(false)
                                   : await _store.QueryAsync(from, to).ConfigureAwait(false);
                if (rows.Count > 0)
                {
                    if (p.step_ms.HasValue && p.step_ms.Value > 0)
                    {
                        static long BucketEnd(long t, long bucketMs) => ((t - 1) / bucketMs + 1) * bucketMs;
                        var bucketMs = useAgg
                            ? (string.Equals(p.agg, "10s", StringComparison.OrdinalIgnoreCase) ? 10_000L : 60_000L)
                            : p.step_ms.Value;
                        resultItems = rows
                            .GroupBy(r => BucketEnd(r.Ts, bucketMs))
                            .OrderBy(g => g.Key)
                            .Select(g =>
                            {
                                var last = g.Last();
                                return new
                                {
                                    ts = g.Key,
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
                        static long BucketEnd(long t, long bucketMs) => ((t - 1) / bucketMs + 1) * bucketMs;
                        var useAggFallback = !string.IsNullOrWhiteSpace(p.agg) && !string.Equals(p.agg, "raw", StringComparison.OrdinalIgnoreCase);
                        var bucketMs = useAggFallback
                            ? (string.Equals(p.agg, "10s", StringComparison.OrdinalIgnoreCase) ? 10_000L : 60_000L)
                            : p.step_ms.Value;
                        resultItems = slice
                            .GroupBy(h => BucketEnd(h.ts, bucketMs))
                            .OrderBy(g => g.Key)
                            .Select(g =>
                            {
                                var last = g.Last();
                                return new
                                {
                                    ts = g.Key,
                                    cpu = want.Contains("cpu") && last.cpu.HasValue ? new { usage_percent = last.cpu.Value } : null,
                                    memory = want.Contains("memory") && last.mem_total.HasValue ? new { total = last.mem_total.Value, used = last.mem_used!.Value } : null
                                } as object;
                            })
                            .ToArray();
                    }
                    else
                    {
                        // 若请求指定了 agg（10s/1m），但 SQLite 无数据，按聚合粒度在内存中对齐到“桶结束时间”，每桶取最后一条
                        var useAggFallback = !string.IsNullOrWhiteSpace(p.agg) && !string.Equals(p.agg, "raw", StringComparison.OrdinalIgnoreCase);
                        if (useAggFallback)
                        {
                            static long BucketEnd(long t, long bucketMs) => ((t - 1) / bucketMs + 1) * bucketMs;
                            var bucketMs = string.Equals(p.agg, "10s", StringComparison.OrdinalIgnoreCase) ? 10_000L : 60_000L;
                            var buckets = slice
                                .GroupBy(h => BucketEnd(h.ts, bucketMs))
                                .OrderBy(g => g.Key)
                                .Select(g =>
                                {
                                    var last = g.Last();
                                    return new
                                    {
                                        ts = g.Key,
                                        cpu = want.Contains("cpu") && last.cpu.HasValue ? new { usage_percent = last.cpu.Value } : null,
                                        memory = want.Contains("memory") && last.mem_total.HasValue ? new { total = last.mem_total.Value, used = last.mem_used!.Value } : null
                                    } as object;
                                })
                                .ToArray();
                            resultItems = buckets;
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
                }
                // 回退：当窗口内没有历史数据
                // - 若未指定聚合（raw 查询），返回一条当前即时值，避免空结果；
                // - 若指定了聚合（10s/1m），返回空数组，保持对齐语义（测试亦允许为空）。
                if (resultItems.Length == 0)
                {
                    var useAggFinal = !string.IsNullOrWhiteSpace(p.agg) && !string.Equals(p.agg, "raw", StringComparison.OrdinalIgnoreCase);
                    if (!useAggFinal)
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
                }
                return new { ok = true, items = resultItems };
            }
        }
        #endif

        // DTOs 已迁移到 Services/DTOs/RpcDtos.cs

        // 内存/CPU 统计 Helper 已迁移到 Helpers/SystemInfo.cs（GetMemoryInfoMb / GetCpuUsagePercent）

        // Win32 互操作已迁移到 Interop/Win32Interop.cs
    }
}
