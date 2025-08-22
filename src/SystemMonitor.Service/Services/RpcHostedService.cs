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

        // DTOs 已迁移到 Services/DTOs/RpcDtos.cs

        // 内存/CPU 统计 Helper 已迁移到 Helpers/SystemInfo.cs（GetMemoryInfoMb / GetCpuUsagePercent）

        // Win32 互操作已迁移到 Interop/Win32Interop.cs
    }
}
