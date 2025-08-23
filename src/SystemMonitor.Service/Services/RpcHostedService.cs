using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using SystemMonitor.Service;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using StreamJsonRpc;
using System.Runtime.Versioning;
using System.Diagnostics;
using System.Linq;
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
            _logger.LogInformation("[startup] RPC HostedService 启动");
            // 初始化历史存储（SQLite）
            _logger.LogInformation("[startup] 初始化 HistoryStore 开始");
            var initSw = Stopwatch.StartNew();
            try { await _store.InitAsync(null, stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "[startup] HistoryStore 初始化失败"); }
            finally { _logger.LogInformation("[startup] 初始化 HistoryStore 结束，用时 {Elapsed}ms", (long)initSw.Elapsed.TotalMilliseconds); }

            var backoffMs = 500;
            var sessions = new List<Task>();

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        _logger.LogInformation("[startup] 创建命名管道实例");
                        var serverStream = CreateSecuredPipe();
                        _logger.LogInformation("[startup] 命名管道已创建，等待客户端连接…");
                        await serverStream.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                        _logger.LogInformation("[startup] 接受到一个客户端连接");

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
            // 允许命名浮点字面量（NaN/Infinity/-Infinity），避免极端值导致序列化失败
            formatter.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
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
            // 后台慢路径预热：周期性刷新物理盘缓存，降低首次采集抖动
            var slowWarmupTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        SystemMonitor.Service.Services.Collectors.DiskCollector.RefreshPhysicalCache();
                    }
                    catch { }
                    try { await Task.Delay(12000, cts.Token).ConfigureAwait(false); } catch { }
                }
            }, cts.Token);
            var metricsTask = Task.Run(async () =>
            {
                var logEvery = GetMetricsLogEvery();
                var consecutiveErrors = 0;
                // 故障注入计数（仅当前会话内生效）
                int simCount = 0; bool simTriggered = false;
                // 会话内模块级缓存：用于超时回退
                var lastModules = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
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
                        // 通过采集器抽象生成各模块字段（带耗时监控 + 有界并发）
                        var swAll = Stopwatch.StartNew();
                        var syncExempt = rpcServer.GetSyncExemptModules();
                        var maxConc = rpcServer.GetMaxConcurrency();
                        var collectors = MetricsRegistry.Collectors.Where(c => enabled.Contains(c.Name)).ToList();
                        var syncList = collectors.Where(c => syncExempt.Contains(c.Name)).ToList();
                        var asyncList = collectors.Where(c => !syncExempt.Contains(c.Name)).ToList();

                        // 同步直采（豁免集）
                        foreach (var c in syncList)
                        {
                            try
                            {
                                var swOne = Stopwatch.StartNew();
                                var val = c.Collect();
                                swOne.Stop();
                                if (val != null)
                                {
                                    payload[c.Name] = val;
                                    lock (lastModules) { lastModules[c.Name] = val; }
                                }
                                if (swOne.ElapsedMilliseconds > 200)
                                {
                                    _logger.LogWarning("collector slow: {Name} took {Elapsed}ms (sync)", c.Name, swOne.ElapsedMilliseconds);
                                }
                            }
                            catch { /* ignore collector error */ }
                        }

                        // 异步并发采集（非豁免集）
                        if (asyncList.Count > 0)
                        {
                            var semaphore = new System.Threading.SemaphoreSlim(Math.Max(1, maxConc));
                            var tasks = new List<Task>();
                            const int defaultTimeoutMs = 300;

                            foreach (var c in asyncList)
                            {
                                tasks.Add(Task.Run(async () =>
                                {
                                    await semaphore.WaitAsync(cts.Token).ConfigureAwait(false);
                                    try
                                    {
                                        var swOne = Stopwatch.StartNew();
                                        var work = Task.Run(() => c.Collect());
                                        var timeoutMs = string.Equals(c.Name, "disk", StringComparison.OrdinalIgnoreCase) ? 800 : defaultTimeoutMs;
                                        var done = await Task.WhenAny(work, Task.Delay(timeoutMs, cts.Token)).ConfigureAwait(false);
                                        if (done == work)
                                        {
                                            var val = await work.ConfigureAwait(false);
                                            swOne.Stop();
                                            if (val != null)
                                            {
                                                lock (payload) { payload[c.Name] = val; }
                                                lock (lastModules) { lastModules[c.Name] = val; }
                                            }
                                            if (swOne.ElapsedMilliseconds > 200)
                                            {
                                                _logger.LogWarning("collector slow: {Name} took {Elapsed}ms (async)", c.Name, swOne.ElapsedMilliseconds);
                                            }
                                        }
                                        else
                                        {
                                            _logger.LogWarning("collector timeout: {Name} exceeded {Timeout}ms, use cached value if any", c.Name, timeoutMs);
                                            lock (lastModules)
                                            {
                                                if (lastModules.TryGetValue(c.Name, out var cached) && cached != null)
                                                {
                                                    lock (payload) { payload[c.Name] = cached; }
                                                }
                                            }
                                        }
                                    }
                                    catch { /* ignore collector error */ }
                                    finally
                                    {
                                        semaphore.Release();
                                    }
                                }, cts.Token));
                            }

                            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* ignore */ }
                        }

                        swAll.Stop();
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
                        // 采集总耗时与当前间隔对比
                        try
                        {
                            var totalMs = (long)swAll.Elapsed.TotalMilliseconds;
                            var intervalMs = rpcServer.GetCurrentIntervalMs(now);
                            if (totalMs > intervalMs)
                            {
                                _logger.LogWarning("metrics collect slow: total {Total}ms exceeds interval {Interval}ms", totalMs, intervalMs);
                            }
                        }
                        catch { }
                        // 若启用了 disk 但未采集到，尝试使用缓存，否则填充占位
                        try
                        {
                            if (enabled.Contains("disk") && !payload.ContainsKey("disk"))
                            {
                                bool filled = false;
                                lock (lastModules)
                                {
                                    if (lastModules.TryGetValue("disk", out var cached) && cached != null)
                                    {
                                        payload["disk"] = cached;
                                        filled = true;
                                    }
                                }
                                if (!filled)
                                {
                                    payload["disk"] = new { status = "warming_up" };
                                }
                            }
                        }
                        catch { }
                        await rpc.NotifyAsync("metrics", payload).ConfigureAwait(false);
                        // 推送成功后，刷新会话缓存
                        try
                        {
                            lock (lastModules)
                            {
                                foreach (var kv in payload)
                                {
                                    if (kv.Key is string key && !string.Equals(key, "ts", StringComparison.OrdinalIgnoreCase) && !string.Equals(key, "seq", StringComparison.OrdinalIgnoreCase))
                                    {
                                        lastModules[key] = kv.Value;
                                    }
                                }
                            }
                        }
                        catch { }
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

        // DTOs 已迁移到 Services/DTOs/RpcDtos.cs

        // 内存/CPU 统计 Helper 已迁移到 Helpers/SystemInfo.cs（GetMemoryInfoMb / GetCpuUsagePercent）

        // Win32 互操作已迁移到 Interop/Win32Interop.cs
    }
}
