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
using static SystemMonitor.Service.Services.Win32Interop;

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
        
        // 采集器统计结构：记录耗时滑窗与计数
        private sealed class CollectorStat
        {
            private readonly Queue<long> _durMs = new();
            private const int MaxSamples = 240; // 约数分钟窗口（视推送频率而定）
            public long SuccessCount;
            public long TimeoutCount;
            public long ErrorCount;
            public long LastMs;

            public void AddDuration(long ms)
            {
                LastMs = ms; SuccessCount++;
                _durMs.Enqueue(Math.Max(0, ms));
                while (_durMs.Count > MaxSamples) _durMs.Dequeue();
            }
            public void AddTimeout() { TimeoutCount++; }
            public void AddError() { ErrorCount++; }
            public object Snapshot()
            {
                var arr = _durMs.ToArray();
                Array.Sort(arr);
                double P(int p)
                {
                    if (arr.Length == 0) return double.NaN;
                    var rank = (p / 100.0) * (arr.Length - 1);
                    int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
                    if (lo == hi) return arr[lo];
                    var w = rank - lo; return arr[lo] * (1 - w) + arr[hi] * w;
                }
                return new
                {
                    last_ms = LastMs,
                    p50_ms = double.IsNaN(P(50)) ? (double?)null : Math.Round(P(50), 1),
                    p95_ms = double.IsNaN(P(95)) ? (double?)null : Math.Round(P(95), 1),
                    p99_ms = double.IsNaN(P(99)) ? (double?)null : Math.Round(P(99), 1),
                    success = SuccessCount,
                    timeout = TimeoutCount,
                    error = ErrorCount
                };
            }
        }
        

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
            
            // 创建会话级别的CancellationTokenSource，确保所有任务都能被正确取消
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var sessionToken = sessionCts.Token;
            
            var connId = Guid.NewGuid();
            var sessionStartTime = DateTimeOffset.UtcNow;
            var allSessionTasks = new List<Task>();
            
            _logger.LogInformation("客户端已连接");

            var reader = serverStream.UsePipeReader();
            var writer = serverStream.UsePipeWriter();
            var formatter = new SystemTextJsonFormatter();
            formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            // 允许命名浮点字面量（NaN/Infinity/-Infinity），避免极端值导致序列化失败
            formatter.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
            var handler = new HeaderDelimitedMessageHandler(writer, reader, formatter);

            _logger.LogInformation("客户端会话建立: conn={ConnId}", connId);
            var rpcServer = new RpcServer(_logger, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _store, connId);
            var rpc = new JsonRpc(handler, rpcServer);
            rpcServer.SetJsonRpc(rpc);

            // 改进的断开连接处理
            rpc.Disconnected += (s, e) =>
            {
                var elapsed = DateTimeOffset.UtcNow - sessionStartTime;
                _logger.LogInformation("客户端断开：{Reason} conn={ConnId}, 连接时长: {Elapsed}ms", 
                    e?.Description ?? "unknown", connId, (long)elapsed.TotalMilliseconds);
                
                // 立即取消所有会话任务
                try { sessionCts.Cancel(); } catch { }
                
                try
                {
                    var reason = string.IsNullOrWhiteSpace(e?.Description) ? "disconnected" : e!.Description!;
                    var payload = new { reason };
                    rpcServer.NotifyBridge("bridge_disconnected", payload);
                }
                catch { /* ignore */ }
            };

            rpc.StartListening();

            // 后台慢路径预热：周期性刷新物理盘缓存，降低首次采集抖动
            var slowWarmupTask = Task.Run(async () =>
            {
                try
                {
                    while (!sessionToken.IsCancellationRequested)
                    {
                        try
                        {
                            SystemMonitor.Service.Services.Collectors.DiskCollector.RefreshPhysicalCache();
                        }
                        catch { }
                        await Task.Delay(12000, sessionToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "预热任务异常结束 conn={ConnId}", connId);
                }
            }, sessionToken);
            allSessionTasks.Add(slowWarmupTask);
            var metricsTask = Task.Run(async () =>
            {
                try
                {
                    var logEvery = GetMetricsLogEvery();
                    var consecutiveErrors = 0;
                    // 故障注入计数（仅当前会话内生效）
                    int simCount = 0; bool simTriggered = false;
                    // 移除本地 lastModules，统一使用 rpcServer 的会话缓存（在 RpcServer 中实现）
                    // 会话内采集器统计：用于首页观测
                    var stats = new Dictionary<string, CollectorStat>(StringComparer.OrdinalIgnoreCase);
                    // 自适应超时：跟踪连续超时次数
                    var consecTimeouts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    // 救火采集抑制：记录最近一次救火时间，避免频繁触发
                    var lastRescueAt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    // 避免救火与原始采集对同一模块并发：记录救火进行中模块
                    var rescueInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    // 紧急刹车机制：连续性能问题计数
                    int consecutiveSlowCycles = 0;
                    int emergencyDelayMs = 1000; // 紧急情况下的采集间隔
                    
                    int pushTick = 0;
                    
                    _logger.LogInformation("开始 metrics 推送循环 conn={ConnId}", connId);
                    
                    while (!sessionToken.IsCancellationRequested)
                {
                    try
                    {
                        pushTick++;
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        
                        // 紧急刹车检查：如果连续出现性能问题，降低采集频率
                        if (consecutiveSlowCycles > 3)
                        {
                            _logger.LogWarning("连续 {Count} 个周期性能不佳，开启紧急刹车模式，延迟 {DelayMs}ms", consecutiveSlowCycles, emergencyDelayMs);
                            await Task.Delay(emergencyDelayMs, sessionToken).ConfigureAwait(false);
                            // 逐渐恢复正常采集间隔
                            emergencyDelayMs = Math.Max(1000, emergencyDelayMs - 200);
                        }
                        // 推送条件：必须是"事件桥接"连接，且开启了 metrics 订阅
                        if (!rpcServer.IsBridgeConnection || !rpcServer.MetricsPushEnabled)
                        {
                            await Task.Delay(300, sessionToken).ConfigureAwait(false);
                            continue;
                        }
                        // 在短期抑制窗口内，避免与当前 RPC 响应交叉
                        if (rpcServer.IsPushSuppressed(now))
                        {
                            await Task.Delay(50, sessionToken).ConfigureAwait(false);
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
                        
                        // 过载保护：如果上次采集耗时过長，跳过昂贵的采集器
                        bool skipExpensive = false;
                        
                        // 检查GPU采集器上次耗时
                        if (stats.TryGetValue("gpu", out var gpuStat) && gpuStat.LastMs > 600) 
                        {
                            skipExpensive = true;
                            _logger.LogWarning("GPU采集器上次耗时 {LastMs}ms，触发过载保护", gpuStat.LastMs);
                        }
                        
                        // 检查Power采集器上次耗时
                        if (stats.TryGetValue("power", out var powerStat) && powerStat.LastMs > 400) 
                        {
                            skipExpensive = true;
                            _logger.LogWarning("Power采集器上次耗时 {LastMs}ms，触发过载保护", powerStat.LastMs);
                        }
                        
                        // 检查整体采集时间趋势
                        var recentStats = stats.Values.Where(s => s.LastMs > 0).ToList();
                        if (recentStats.Count > 0)
                        {
                            var totalLastMs = recentStats.Sum(s => s.LastMs);
                            if (totalLastMs > 1200) // 总耗时超过1.2秒
                            {
                                skipExpensive = true;
                                _logger.LogWarning("总采集耗时 {TotalMs}ms 过高，触发过载保护", totalLastMs);
                            }
                        }
                        
                        // 如果过载，优先采集关键指标
                        if (skipExpensive) {
                            collectors = collectors.Where(c => 
                                c.Name.Equals("cpu", StringComparison.OrdinalIgnoreCase) ||
                                c.Name.Equals("memory", StringComparison.OrdinalIgnoreCase) ||
                                c.Name.Equals("disk", StringComparison.OrdinalIgnoreCase)
                            ).ToList();
                            _logger.LogWarning("检测到系统过载，跳过昂贵采集器 (GPU/Power)");
                        }
                        
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
                                    rpcServer.SetModuleCache(c.Name, val);
                                }
                                if (swOne.ElapsedMilliseconds > 200)
                                {
                                    _logger.LogWarning("collector slow: {Name} took {Elapsed}ms (sync)", c.Name, swOne.ElapsedMilliseconds);
                                }
                                // 记录统计
                                var st = stats.TryGetValue(c.Name, out var s) ? s : (stats[c.Name] = new CollectorStat());
                                st.AddDuration(swOne.ElapsedMilliseconds);
                            }
                            catch
                            {
                                var st = stats.TryGetValue(c.Name, out var s) ? s : (stats[c.Name] = new CollectorStat());
                                st.AddError();
                                /* ignore collector error */
                            }
                        }

                        // 异步并发采集（非豁免集）
                        if (asyncList.Count > 0)
                        {
                            var semaphore = new System.Threading.SemaphoreSlim(Math.Max(1, maxConc));
                            var tasks = new List<Task>();
                            const int defaultTimeoutMs = 300;
                            
                            // 使用会话级别的CancellationToken确保任务能被正确取消

                            foreach (var c in asyncList)
                            {
                                tasks.Add(Task.Run(async () =>
                                {
                                    await semaphore.WaitAsync(sessionToken).ConfigureAwait(false);
                                    try
                                    {
                                        var swOne = Stopwatch.StartNew();
                                        var work = Task.Run(() => c.Collect());
                                        int timeoutMs = defaultTimeoutMs;
                                        // 基础阈值：更激进的超时配置，快速失败避免堆积
                                        if (string.Equals(c.Name, "disk", StringComparison.OrdinalIgnoreCase)) timeoutMs = 400;
                                        else if (string.Equals(c.Name, "power", StringComparison.OrdinalIgnoreCase)) timeoutMs = 500;
                                        else if (string.Equals(c.Name, "gpu", StringComparison.OrdinalIgnoreCase)) timeoutMs = 600;
                                        else if (string.Equals(c.Name, "network", StringComparison.OrdinalIgnoreCase)) timeoutMs = 500;
                                        // 自适应：若该模块连续超时达到阈值，则临时提升超时以抓到首次数据
                                        int ct;
                                        lock (consecTimeouts) { consecTimeouts.TryGetValue(c.Name, out ct); }
                                        if (ct >= 2)
                                        {
                                            if (string.Equals(c.Name, "gpu", StringComparison.OrdinalIgnoreCase)) timeoutMs = Math.Max(timeoutMs, 1200);
                                            if (string.Equals(c.Name, "power", StringComparison.OrdinalIgnoreCase)) timeoutMs = Math.Max(timeoutMs, 1000);
                                        }
                                        // 亦可在会话刚开始的前几次推送稍微放宽（优化后：更保守的初始超时）
                                        if (pushTick < 3)
                                        {
                                            if (string.Equals(c.Name, "gpu", StringComparison.OrdinalIgnoreCase)) timeoutMs = Math.Max(timeoutMs, 1200);
                                            if (string.Equals(c.Name, "power", StringComparison.OrdinalIgnoreCase)) timeoutMs = Math.Max(timeoutMs, 1000);
                                        }
                                        var done = await Task.WhenAny(work, Task.Delay(timeoutMs, sessionToken)).ConfigureAwait(false);
                                        if (done == work)
                                        {
                                            var val = await work.ConfigureAwait(false);
                                            swOne.Stop();
                                            if (val != null)
                                            {
                                                lock (payload) { payload[c.Name] = val; }
                                                rpcServer.SetModuleCache(c.Name, val);
                                            }
                                            // 成功则重置连续超时计数
                                            lock (consecTimeouts) { consecTimeouts[c.Name] = 0; }
                                            if (swOne.ElapsedMilliseconds > 200)
                                            {
                                                _logger.LogWarning("collector slow: {Name} took {Elapsed}ms (async)", c.Name, swOne.ElapsedMilliseconds);
                                            }
                                            // 记录统计
                                            var st = stats.TryGetValue(c.Name, out var s) ? s : (stats[c.Name] = new CollectorStat());
                                            st.AddDuration(swOne.ElapsedMilliseconds);
                                        }
                                        else
                                        {
                                            _logger.LogWarning("collector timeout: {Name} exceeded {Timeout}ms, use cached value if any", c.Name, timeoutMs);
                                            if (rpcServer.TryGetModuleFromCache(c.Name, out var cached) && cached != null)
                                            {
                                                lock (payload) { payload[c.Name] = cached; }
                                            }
                                            // 若连续超时较多，触发一次救火采集（更大超时），尝试播种缓存
                                            int ct2; long nowTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                            lock (consecTimeouts) { consecTimeouts.TryGetValue(c.Name, out ct2); }
                                            bool shouldRescue = ct2 >= 3;
                                            if (shouldRescue)
                                            {
                                                bool allowed = false;
                                                lock (lastRescueAt)
                                                {
                                                    lastRescueAt.TryGetValue(c.Name, out var lastTs);
                                                    if (lastTs == 0 || nowTs - lastTs >= 5000) // 同一模块5秒内最多一次
                                                    {
                                                        lastRescueAt[c.Name] = nowTs;
                                                        allowed = true;
                                                    }
                                                }
                                                if (allowed)
                                                {
                                                    bool doRescue = false;
                                                    lock (rescueInFlight)
                                                    {
                                                        if (!rescueInFlight.Contains(c.Name))
                                                        {
                                                            rescueInFlight.Add(c.Name);
                                                            doRescue = true;
                                                        }
                                                    }
                                                    if (doRescue)
                                                    {
                                                        int rescueTimeout = timeoutMs;
                                                        // 优化后：更保守的救火超时配置
                                                        if (string.Equals(c.Name, "gpu", StringComparison.OrdinalIgnoreCase)) rescueTimeout = Math.Max(rescueTimeout, 1500);
                                                        else if (string.Equals(c.Name, "power", StringComparison.OrdinalIgnoreCase)) rescueTimeout = Math.Max(rescueTimeout, 1200);
                                                        else if (string.Equals(c.Name, "network", StringComparison.OrdinalIgnoreCase)) rescueTimeout = Math.Max(rescueTimeout, 1000);
                                                        try
                                                        {
                                                            _logger.LogWarning("collector rescue: {Name} try with extended timeout {Timeout}ms", c.Name, rescueTimeout);
                                                            var rescueTask = Task.Run(() => c.Collect());
                                                            var rescueDone = await Task.WhenAny(rescueTask, Task.Delay(rescueTimeout, sessionToken)).ConfigureAwait(false);
                                                            if (rescueDone == rescueTask)
                                                            {
                                                                var val2 = await rescueTask.ConfigureAwait(false);
                                                                if (val2 != null)
                                                                {
                                                                    lock (payload) { payload[c.Name] = val2; }
                                                                    rpcServer.SetModuleCache(c.Name, val2);
                                                                    lock (consecTimeouts) { consecTimeouts[c.Name] = 0; }
                                                                    _logger.LogInformation("collector rescue success: {Name}", c.Name);
                                                                }
                                                            }
                                                            else
                                                            {
                                                                _logger.LogWarning("collector rescue timeout: {Name} exceeded {Timeout}ms", c.Name, rescueTimeout);
                                                            }
                                                        }
                                                        catch { /* ignore rescue errors */ }
                                                        finally
                                                        {
                                                            lock (rescueInFlight) { rescueInFlight.Remove(c.Name); }
                                                        }
                                                    }
                                                }
                                            }
                                            // 递增连续超时计数
                                            lock (consecTimeouts)
                                            {
                                                consecTimeouts.TryGetValue(c.Name, out var n);
                                                consecTimeouts[c.Name] = n + 1;
                                            }
                                            var st = stats.TryGetValue(c.Name, out var s) ? s : (stats[c.Name] = new CollectorStat());
                                            st.AddTimeout();
                                        }
                                    }
                                    catch
                                    {
                                        var st = stats.TryGetValue(c.Name, out var s) ? s : (stats[c.Name] = new CollectorStat());
                                        st.AddError();
                                        /* ignore collector error */
                                    }
                                    finally
                                    {
                                        semaphore.Release();
                                    }
                                }, sessionToken));
                            }

                            try 
                            { 
                                // 等待所有异步采集任务完成，但有超时保护
                                var allTasksCompletion = Task.WhenAll(tasks);
                                var timeoutTask = Task.Delay(10000, sessionToken); // 10秒超时
                                var completed = await Task.WhenAny(allTasksCompletion, timeoutTask).ConfigureAwait(false);
                                
                                if (completed == timeoutTask)
                                {
                                    _logger.LogWarning("异步采集任务整体超时，强制继续 conn={ConnId}", connId);
                                }
                            } 
                            catch (OperationCanceledException) 
                            {
                                _logger.LogInformation("异步采集任务被取消 conn={ConnId}", connId);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "异步采集任务异常 conn={ConnId}", connId);
                            }
                            finally
                            {
                                semaphore.Dispose();
                            }
                        }

                        swAll.Stop();
                        var totalCollectMs = (long)swAll.Elapsed.TotalMilliseconds;
                        
                        // 紧急刹车机制：更新性能计数器
                        if (totalCollectMs > 1500) // 单次采集超过1.5秒
                        {
                            consecutiveSlowCycles++;
                            emergencyDelayMs = Math.Min(3000, emergencyDelayMs + 300); // 逐渐增加延迟
                            _logger.LogWarning("本次采集耗时 {TotalMs}ms，连续慢周期计数: {Count}", totalCollectMs, consecutiveSlowCycles);
                        }
                        else if (totalCollectMs < 800) // 正常采集时间
                        {
                            consecutiveSlowCycles = Math.Max(0, consecutiveSlowCycles - 1); // 逐渐恢复
                            if (consecutiveSlowCycles == 0)
                            {
                                emergencyDelayMs = 1000; // 重置为默认延迟
                            }
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
                        // 若启用了 gpu 且已采集到，将其克隆到 gpu_raw 方便前端首页直接展示 JSON
                        try
                        {
                            if (enabled.Contains("gpu") && payload.ContainsKey("gpu"))
                            {
                                payload["gpu_raw"] = payload["gpu"]; // 引用同一对象，避免额外序列化成本
                            }
                        }
                        catch { }

                        // 若启用了 gpu 但未采集到，尝试使用缓存（避免快照/首页缺 gpu）
                        try
                        {
                            if (enabled.Contains("gpu") && !payload.ContainsKey("gpu"))
                            {
                                if (rpcServer.TryGetModuleFromCache("gpu", out var cached) && cached != null)
                                {
                                    payload["gpu"] = cached;
                                    payload["gpu_raw"] = cached;
                                }
                            }
                        }
                        catch { }

                        // 若启用了 disk 但未采集到，尝试使用缓存，否则填充占位
                        try
                        {
                            if (enabled.Contains("disk") && !payload.ContainsKey("disk"))
                            {
                                bool filled = false;
                                if (rpcServer.TryGetModuleFromCache("disk", out var cached) && cached != null)
                                {
                                    payload["disk"] = cached;
                                    filled = true;
                                }
                                if (!filled)
                                {
                                    payload["disk"] = new { status = "warming_up" };
                                }
                            }
                        }
                        catch { }
                        // 若启用了 power 但未采集到，尝试使用缓存；如无，则使用 GetSystemPowerStatus 构造轻量级占位，避免前端全为“—”
                        try
                        {
                            if (enabled.Contains("power") && !payload.ContainsKey("power"))
                            {
                                bool filled = false;
                                if (rpcServer.TryGetModuleFromCache("power", out var cached) && cached != null)
                                {
                                    payload["power"] = cached;
                                    filled = true;
                                }
                                if (!filled)
                                {
                                    bool? ac = null; double? pct = null; int? remainMin = null; int? toFullMin = null;
                                    try
                                    {
                                        if (GetSystemPowerStatus(out var sps))
                                        {
                                            ac = sps.ACLineStatus == 0 ? false : sps.ACLineStatus == 1 ? true : (bool?)null;
                                            pct = sps.BatteryLifePercent == 255 ? (double?)null : sps.BatteryLifePercent;
                                            remainMin = sps.BatteryLifeTime >= 0 ? (int?)Math.Max(0, sps.BatteryLifeTime / 60) : null;
                                            toFullMin = sps.BatteryFullLifeTime >= 0 ? (int?)Math.Max(0, sps.BatteryFullLifeTime / 60) : null;
                                        }
                                    }
                                    catch { }
                                    var battery = new
                                    {
                                        percentage = pct,
                                        state = (string?)null,
                                        time_remaining_min = remainMin,
                                        time_to_full_min = toFullMin,
                                        ac_line_online = ac,
                                        time_on_battery_sec = (int?)null,
                                        temperature_c = (double?)null,
                                        cycle_count = (int?)null,
                                        condition = (string?)null,
                                        full_charge_capacity_mah = (double?)null,
                                        design_capacity_mah = (double?)null,
                                        voltage_mv = (double?)null,
                                        current_ma = (double?)null,
                                        power_w = (double?)null,
                                        manufacturer = (string?)null,
                                        serial_number = (string?)null,
                                        manufacture_date = (string?)null,
                                    };
                                    payload["power"] = new { battery, adapter = (object?)null, ups = (object?)null, usb = (object?)null };
                                }
                            }
                        }
                        catch { }
                        // 附加：采集器统计诊断（供前端首页展示）
                        try
                        {
                            var diag = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                            foreach (var kv in stats)
                            {
                                diag[kv.Key] = kv.Value.Snapshot();
                            }
                            payload["collectors_diag"] = diag;
                        }
                        catch { /* ignore diag build error */ }

                        await rpc.NotifyAsync("metrics", payload).ConfigureAwait(false);
                        // 推送成功后，刷新会话缓存
                        try
                        {
                            rpcServer.UpdateModuleCacheFromPayload(payload.ToDictionary(k => k.Key, v => v.Value));
                        }
                        catch { }
                        var pushed = rpcServer.IncrementMetricsCount();
                        pushTick++;
                        if (logEvery > 0 && pushed % logEvery == 0)
                        {
                            _logger.LogInformation("metrics 推送累计: {Count}", pushed);
                        }
                        // 使用“目标间隔 - 本次采集耗时”的补偿延迟，避免周期累加
                        var now2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var targetInterval = rpcServer.GetCurrentIntervalMs(now2);
                        var spentMs = (int)Math.Max(0, swAll.ElapsedMilliseconds);
                        var delay = Math.Max(0, targetInterval - spentMs);
                        await Task.Delay(delay, sessionToken).ConfigureAwait(false);
                        consecutiveErrors = 0; // 只要成功一次就清零
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInformation("metrics 推送循环被取消 conn={ConnId}", connId);
                        break;
                    }
                    catch (Exception ex)
                    {
                        consecutiveErrors++;
                        _logger.LogWarning(ex, "metrics 推送异常 conn={ConnId}, 连续错误次数: {ConsecutiveErrors}", connId, consecutiveErrors);
                        
                        if (consecutiveErrors > 10)
                        {
                            _logger.LogError("metrics 推送连续错误过多，延长等待时间 conn={ConnId}", connId);
                            try { await Task.Delay(1000, sessionToken).ConfigureAwait(false); } catch { break; }
                        }
                    }
                }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "metrics 任务外层异常 conn={ConnId}", connId);
                }
                finally
                {
                    _logger.LogInformation("metrics 推送循环结束 conn={ConnId}", connId);
                }
            }, sessionToken);
            allSessionTasks.Add(metricsTask);

            try
            {
                // 等待RPC完成
                await rpc.Completion.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RPC会话异常结束 conn={ConnId}", connId);
            }
            finally
            {
                // 会话结束时强制清理所有后台任务
                var cleanupStartTime = DateTimeOffset.UtcNow;
                _logger.LogInformation("开始清理会话任务 conn={ConnId}, 任务数量: {TaskCount}", connId, allSessionTasks.Count);
                
                // 第一步：取消所有任务
                try 
                { 
                    sessionCts.Cancel(); 
                    _logger.LogDebug("已发送取消信号 conn={ConnId}", connId);
                } 
                catch { }
                
                // 第二步：等待任务优雅结束（5秒超时）
                try
                {
                    var gracefulCompletion = Task.WhenAll(allSessionTasks);
                    await gracefulCompletion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    var cleanupElapsed = DateTimeOffset.UtcNow - cleanupStartTime;
                    _logger.LogInformation("会话任务优雅清理完成 conn={ConnId}, 耗时: {Elapsed}ms", connId, (long)cleanupElapsed.TotalMilliseconds);
                }
                catch (TimeoutException)
                {
                    var cleanupElapsed = DateTimeOffset.UtcNow - cleanupStartTime;
                    _logger.LogWarning("会话任务清理超时 conn={ConnId}, 超时时间: {Elapsed}ms, 将强制结束", connId, (long)cleanupElapsed.TotalMilliseconds);
                    
                    // 第三步：强制垃圾回收，帮助清理未完成的任务
                    try
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    var cleanupElapsed = DateTimeOffset.UtcNow - cleanupStartTime;
                    _logger.LogError(ex, "会话任务清理异常 conn={ConnId}, 耗时: {Elapsed}ms", connId, (long)cleanupElapsed.TotalMilliseconds);
                }
                
                // 最终清理
                try
                {
                    rpc?.Dispose();
                }
                catch { }
                
                var totalElapsed = DateTimeOffset.UtcNow - sessionStartTime;
                _logger.LogInformation("客户端会话完全结束 conn={ConnId}, 总时长: {Elapsed}ms", connId, (long)totalElapsed.TotalMilliseconds);
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
