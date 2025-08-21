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
        private static readonly object _collectorLock = new object();
        private static readonly List<string> _activeModules = new List<string>();
        private static readonly List<JsonRpc> _bridgeConnections = new List<JsonRpc>();

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

        // -------------------------
        // 每核频率采样器（MHz）
        // 基于 Processor Information/% Processor Performance 的每核百分比，乘以 max_mhz 估算当前 MHz
        // -------------------------
        private sealed class PerCoreFrequency
        {
            private static readonly Lazy<PerCoreFrequency> _inst = new(() => new PerCoreFrequency());
            public static PerCoreFrequency Instance => _inst.Value;

            private System.Diagnostics.PerformanceCounter[]? _perfPct;
            private long _lastTicks;
            private int?[] _last = Array.Empty<int?>();
            private bool _initTried;

            private void EnsureInit()
            {
                if (_initTried) return; _initTried = true;
                try
                {
                    var cat = new System.Diagnostics.PerformanceCounterCategory("Processor Information");
                    var instances = cat.GetInstanceNames()
                        .Where(n => !string.Equals(n, "_Total", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    _perfPct = instances
                        .Select(n => new System.Diagnostics.PerformanceCounter("Processor Information", "% Processor Performance", n, readOnly: true))
                        .ToArray();
                    // 预热
                    foreach (var c in _perfPct) { try { _ = c.NextValue(); } catch { } }
                    _lastTicks = Environment.TickCount64;
                    _last = new int?[instances.Length];
                }
                catch
                {
                    _perfPct = Array.Empty<System.Diagnostics.PerformanceCounter>();
                    _last = Array.Empty<int?>();
                }
            }

            public int?[] Read()
            {
                EnsureInit();
                var now = Environment.TickCount64;
                if (now - _lastTicks < 500) return _last;

                var (_, maxMHz) = CpuFrequency.Instance.Read();
                if (_perfPct == null || _perfPct.Length == 0 || !maxMHz.HasValue)
                {
                    _lastTicks = now; return _last;
                }
                var arr = new int?[_perfPct.Length];
                for (int i = 0; i < _perfPct.Length; i++)
                {
                    try
                    {
                        var pct = Math.Clamp(_perfPct[i].NextValue(), 0.0f, 200.0f); // 允许睿频 >100%
                        arr[i] = (int)Math.Max(0, Math.Round(maxMHz.Value * (pct / 100.0)));
                    }
                    catch { arr[i] = null; }
                }
                _last = arr; _lastTicks = now; return _last;
            }
        }

        // -------------------------
        // CPU 传感器（最小可行：包温度）
        // -------------------------
        private sealed class CpuSensors
        {
            private static readonly Lazy<CpuSensors> _inst = new(() => new CpuSensors());
            public static CpuSensors Instance => _inst.Value;
            private long _lastTicks;
            private double? _lastPackageTempC;

            public double? Read()
            {
                var now = Environment.TickCount64;
                if (now - _lastTicks < 2_000) return _lastPackageTempC;
                double? pkg = null;
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher("ROOT\\WMI", "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        try
                        {
                            var tenthKelvin = Convert.ToInt64(obj["CurrentTemperature"]); // 0.1 Kelvin
                            if (tenthKelvin > 0)
                            {
                                var c = (tenthKelvin / 10.0) - 273.15;
                                if (!double.IsNaN(c) && c > -50 && c < 150)
                                    pkg = Math.Max(pkg ?? double.MinValue, c);
                            }
                        }
                        catch { }
                    }
                }
                catch { /* ignore */ }
                _lastPackageTempC = pkg; _lastTicks = now; return _lastPackageTempC;
            }
        }

        // -------------------------
        // 内核活动计数器采样器（每秒速率）
        // -------------------------
        private sealed class KernelActivitySampler
        {
            private static readonly Lazy<KernelActivitySampler> _inst = new(() => new KernelActivitySampler());
            public static KernelActivitySampler Instance => _inst.Value;

            private readonly object _lock = new();
            private long _lastTicks;
            private (double? ctx, double? sysc, double? intr) _lastValues;
            private bool _initTried;

            // PerformanceCounter 引用
            private PerformanceCounter? _pcCtx;
            private PerformanceCounter? _pcSyscalls;
            private PerformanceCounter? _pcIntr;

            public (double? contextSwitchesPerSec, double? syscallsPerSec, double? interruptsPerSec) Read()
            {
                var now = Environment.TickCount64;
                lock (_lock)
                {
                    if (now - _lastTicks < 200)
                    {
                        return _lastValues;
                    }

                    EnsureInit();

                    double? ctx = null, sysc = null, intr = null;
                    try { if (_pcCtx != null) ctx = Math.Max(0, _pcCtx.NextValue()); } catch { ctx = null; }
                    try { if (_pcSyscalls != null) sysc = Math.Max(0, _pcSyscalls.NextValue()); } catch { sysc = null; }
                    try { if (_pcIntr != null) intr = Math.Max(0, _pcIntr.NextValue()); } catch { intr = null; }

                    _lastValues = (ctx, sysc, intr);
                    _lastTicks = now;
                    return _lastValues;
                }
            }

            private void EnsureInit()
            {
                if (_initTried) return;
                _initTried = true;
                try
                {
                    // 优先 System 类别
                    _pcCtx = TryCreateCounter("System", "Context Switches/sec");
                    _pcSyscalls = TryCreateCounter("System", "System Calls/sec");
                    _pcIntr = TryCreateCounter("System", "Interrupts/sec");

                    // 回退到 Processor Information/_Total 某些环境计数器
                    if (_pcCtx == null)
                        _pcCtx = TryCreateCounter("Processor Information", "Context Switches/sec", "_Total");
                    if (_pcSyscalls == null)
                        _pcSyscalls = TryCreateCounter("Processor Information", "System Calls/sec", "_Total");
                    if (_pcIntr == null)
                        _pcIntr = TryCreateCounter("Processor Information", "Interrupts/sec", "_Total");
                }
                catch
                {
                    // ignore
                }
            }

            private static PerformanceCounter? TryCreateCounter(string category, string counter, string? instance = null)
            {
                try
                {
                    if (instance == null)
                        return new PerformanceCounter(category, counter, readOnly: true);
                    else
                        return new PerformanceCounter(category, counter, instance, readOnly: true);
                }
                catch
                {
                    return null;
                }
            }
        }

        private sealed class PerCoreCounters
        {
            private static readonly Lazy<PerCoreCounters> _inst = new(() => new PerCoreCounters());
            public static PerCoreCounters Instance => _inst.Value;
            private System.Diagnostics.PerformanceCounter[]? _cores;
            private long _lastTicks;
            private double[] _last = Array.Empty<double>();
            private bool _initTried;
            private void EnsureInit()
            {
                if (_initTried) return; _initTried = true;
                try
                {
                    var cat = new System.Diagnostics.PerformanceCounterCategory("Processor");
                    var names = cat.GetInstanceNames();
                    var coreNames = names.Where(n => !string.Equals(n, "_Total", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n).ToArray();
                    var list = new List<System.Diagnostics.PerformanceCounter>();
                    foreach (var n in coreNames)
                    {
                        try { list.Add(new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", n, readOnly: true)); } catch { }
                    }
                    _cores = list.ToArray();
                    if (_cores != null) foreach (var c in _cores) { try { _ = c.NextValue(); } catch { } }
                    _last = new double[_cores?.Length ?? 0];
                    _lastTicks = Environment.TickCount64;
                } catch { }
            }
            public double[] Read()
            {
                EnsureInit(); var now = Environment.TickCount64;
                if (now - _lastTicks < 500) return _last;
                if (_cores == null || _cores.Length == 0) { _last = Array.Empty<double>(); _lastTicks = now; return _last; }
                var vals = new double[_cores.Length];
                for (int i = 0; i < _cores.Length; i++)
                {
                    try { vals[i] = Math.Clamp(_cores[i].NextValue(), 0.0f, 100.0f); } catch { vals[i] = 0.0; }
                }
                _last = vals; _lastTicks = now; return _last;
            }
        }

        private sealed class CpuFrequency
        {
            private static readonly Lazy<CpuFrequency> _inst = new(() => new CpuFrequency());
            public static CpuFrequency Instance => _inst.Value;
            private long _lastTicks;
            private (int? cur, int? max) _last;
            private bool _initTried;
            private System.Diagnostics.PerformanceCounter? _pcFreq; // Processor Frequency (_Total)
            private System.Diagnostics.PerformanceCounter? _pcPerfPct; // % Processor Performance (_Total)
            private int? _busMhz; // ExtClock from WMI
            private int? _minMhz; // MinClockSpeed from WMI (if available)
            public (int? cur, int? max) Read()
            {
                var now = Environment.TickCount64;
                if (now - _lastTicks < 1_000) return _last;
                EnsureInit();
                int? cur = null, max = null;
                // 优先使用 PerformanceCounter 的动态频率
                try { if (_pcFreq != null) cur = Math.Max(0, Convert.ToInt32(_pcFreq.NextValue())); } catch { cur = null; }
                // 使用 WMI 作为回退，并同时获取 max
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher("SELECT CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor");
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        try { var wCur = Convert.ToInt32(obj["CurrentClockSpeed"]); cur = cur ?? Math.Max(0, wCur); } catch { }
                        try { var wMax = Convert.ToInt32(obj["MaxClockSpeed"]); max = Math.Max(max ?? 0, wMax); } catch { }
                    }
                }
                catch { /* ignore */ }
                // 若仍缺少当前频率，尝试用 % Processor Performance * max_mhz 估算
                if (cur == null)
                {
                    try
                    {
                        if (_pcPerfPct != null)
                        {
                            var pct = Math.Max(0.0f, Math.Min(100.0f, _pcPerfPct.NextValue()));
                            if (max.HasValue)
                            {
                                cur = (int)Math.Max(0, Math.Round(max.Value * (pct / 100.0)));
                            }
                        }
                    }
                    catch { /* ignore */ }
                }
                _last = (cur, max); _lastTicks = now; return _last;
            }
            public int? ReadBusMhz()
            {
                if (_busMhz.HasValue) return _busMhz;
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher("SELECT ExtClock FROM Win32_Processor");
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        try { var v = Convert.ToInt32(obj["ExtClock"]); if (v > 0) { _busMhz = v; break; } } catch { }
                    }
                }
                catch { }
                return _busMhz;
            }
            public int? ReadMinMhz()
            {
                if (_minMhz.HasValue) return _minMhz;
                try
                {
                    using var searcher = new System.Management.ManagementObjectSearcher("SELECT MinClockSpeed FROM Win32_Processor");
                    foreach (System.Management.ManagementObject obj in searcher.Get())
                    {
                        try { var v = Convert.ToInt32(obj["MinClockSpeed"]); if (v > 0) { _minMhz = v; break; } } catch { }
                    }
                }
                catch { }
                return _minMhz;
            }
            private void EnsureInit()
            {
                if (_initTried) return; _initTried = true;
                try
                {
                    _pcFreq = TryCreateCounter("Processor Information", "Processor Frequency", "_Total");
                    if (_pcFreq == null)
                        _pcFreq = TryCreateCounter("Processor", "Processor Frequency", "_Total");
                    // 预备：% Processor Performance
                    _pcPerfPct = TryCreateCounter("Processor Information", "% Processor Performance", "_Total");
                }
                catch { /* ignore */ }
            }
            private static System.Diagnostics.PerformanceCounter? TryCreateCounter(string category, string counter, string? instance = null)
            {
                try
                {
                    if (instance == null)
                        return new System.Diagnostics.PerformanceCounter(category, counter, readOnly: true);
                    else
                        return new System.Diagnostics.PerformanceCounter(category, counter, instance, readOnly: true);
                }
                catch
                {
                    return null;
                }
            }
        }

        private sealed class CpuLoadAverages
        {
            private static readonly Lazy<CpuLoadAverages> _inst = new(() => new CpuLoadAverages());
            public static CpuLoadAverages Instance => _inst.Value;
            private double _l1, _l5, _l15;
            private long _lastTs;
            public (double l1, double l5, double l15) Update(double usagePercent)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var dtSec = Math.Max(0.05, (now - _lastTs) / 1000.0);
                // 将 usage_percent(0~100) 归一化到 0~1 再做 EWMA
                var x = Math.Clamp(usagePercent / 100.0, 0.0, 1.0);
                double step(double last, double win) { var alpha = 1 - Math.Exp(-dtSec / win); return last + alpha * (x - last); }
                _l1 = step(_l1, 60.0); _l5 = step(_l5, 300.0); _l15 = step(_l15, 900.0);
                _lastTs = now;
                // 输出仍然以 0~100 百分比表示
                return (_l1 * 100.0, _l5 * 100.0, _l15 * 100.0);
            }
        }

        /// <summary>
        /// 采集系统指标并推送给所有桥接连接。
        /// </summary>
        private static void CollectAndPushMetrics(object? state)
        {
            try
            {
                List<string> modules;
                List<JsonRpc> connections;
                
                lock (_collectorLock)
                {
                    if (_activeModules.Count == 0) return;
                    modules = new List<string>(_activeModules);
                    connections = new List<JsonRpc>(_bridgeConnections.Where(c => !c.IsDisposed));
                }

                if (connections.Count == 0) return;

                var metrics = new Dictionary<string, object>();
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                foreach (var module in modules)
                {
                    switch (module.ToLowerInvariant())
                    {
                        case "cpu":
                            using (var process = Process.GetCurrentProcess())
                            {
                                var cpuTime = process.TotalProcessorTime.TotalMilliseconds;
                                metrics["cpu"] = new { usage_percent = Math.Round(cpuTime / 1000.0 % 100, 2) };
                            }
                            break;
                        case "mem":
                            var workingSet = GC.GetTotalMemory(false);
                            metrics["mem"] = new { used_bytes = workingSet, used_mb = Math.Round(workingSet / 1024.0 / 1024.0, 2) };
                            break;
                    }
                }

                var payload = new { timestamp, metrics };

                // 推送给所有桥接连接
                foreach (var connection in connections)
                {
                    try
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await connection.NotifyAsync("metrics", payload);
                            }
                            catch { /* 忽略推送失败 */ }
                        });
                    }
                    catch { /* 忽略连接异常 */ }
                }
            }
            catch { /* 忽略采集异常 */ }
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

        // CPU breakdown（user/system/idle）
        private static readonly object _cpuBkLock = new();
        private static bool _cpuBkInit;
        private static ulong _bkPrevIdle, _bkPrevKernel, _bkPrevUser;
        private static (double user, double sys, double idle) GetCpuBreakdownPercent()
        {
            try
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user)) return (0, 0, 0);
                static ulong ToU64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
                var iNow = ToU64(idle); var kNow = ToU64(kernel); var uNow = ToU64(user);
                double u = 0, s = 0, id = 0;
                lock (_cpuBkLock)
                {
                    if (_cpuBkInit)
                    {
                        var iD = iNow - _bkPrevIdle;
                        var kD = kNow - _bkPrevKernel;
                        var uD = uNow - _bkPrevUser;
                        var total = kD + uD;
                        if (total > 0)
                        {
                            var busy = total > iD ? (total - iD) : 0UL;
                            // kernel 部分的 busy 近似为 (kD - iD)，user 为 uD
                            var kBusy = kD > iD ? (kD - iD) : 0UL;
                            u = 100.0 * uD / total;
                            s = 100.0 * kBusy / total;
                            var used = busy;
                            id = 100.0 * (total - used) / total;
                            u = Math.Clamp(u, 0.0, 100.0);
                            s = Math.Clamp(s, 0.0, 100.0);
                            id = Math.Clamp(id, 0.0, 100.0);
                        }
                    }
                    _bkPrevIdle = iNow; _bkPrevKernel = kNow; _bkPrevUser = uNow; _cpuBkInit = true;
                }
                return (u, s, id);
            }
            catch { return (0, 0, 0); }
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

            var connId = Guid.NewGuid();
            _logger.LogInformation("客户端会话建立: conn={ConnId}", connId);
            var rpcServer = new RpcServer(_logger, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _store, connId);
            var rpc = new JsonRpc(handler, rpcServer);
            rpcServer.SetJsonRpc(rpc);

            rpc.Disconnected += (s, e) =>
            {
                // 从桥接连接列表中移除
                lock (_collectorLock) { _bridgeConnections.RemoveAll(c => c == rpc || c.IsDisposed); }
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
                        // 仅向“事件桥”连接推送，避免与短连接响应混流
                        if (!rpcServer.MetricsPushEnabled || !rpcServer.IsBridgeConnection)
                        {
                            await Task.Delay(300, cts.Token).ConfigureAwait(false);
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
                        foreach (var c in s_collectors)
                        {
                            if (!enabled.Contains(c.Name)) continue;
                            var val = c.Collect();
                            if (val != null) payload[c.Name] = val;
                        }
                        // 维持历史/持久化所需的 CPU/Memory 数值（避免从匿名对象中反射）
                        if (enabled.Contains("cpu"))
                        {
                            var cpu = GetCpuUsagePercent();
                            cpuVal = cpu;
                        }
                        if (enabled.Contains("memory"))
                        {
                            var mem = GetMemoryInfoMb();
                            memVal = (mem.total_mb, mem.used_mb);
                        }
                        if (cpuVal.HasValue || memVal.HasValue)
                        {
                            rpcServer.AppendHistory(now, cpuVal, memVal);
                            // 持久化到 SQLite（忽略错误，不影响推送）
                            try { await rpcServer.PersistHistoryAsync(now, cpuVal, memVal).ConfigureAwait(false); } catch { }
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
                foreach (var c in s_collectors)
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
            public string? agg { get; set; } // 'raw' | '10s' | '1m'
        }
        
        public sealed class SubscribeMetricsParams
        {
            public bool enable { get; set; }
        }
        #endregion

        // 统一采集接口与内置采集器
        private interface IMetricsCollector
        {
            string Name { get; }
            object? Collect();
        }

        private sealed class CpuCollector : IMetricsCollector
        {
            public string Name => "cpu";
            public object? Collect()
            {
                // 总使用率
                var usage = GetCpuUsagePercent();
                // 分解 user/system/idle
                var (userPct, sysPct, idlePct) = GetCpuBreakdownPercent();
                // uptime 秒
                long uptimeSec = Math.Max(0, (long)(Environment.TickCount64 / 1000));
                // 进程/线程计数
                var (proc, threads) = SystemCounters.Instance.ReadProcThread();
                // per-core
                var perCore = PerCoreCounters.Instance.Read();
                // 频率（MHz）
                var (curMHzRaw, maxMHz) = CpuFrequency.Instance.Read();
                var perCoreFreq = PerCoreFrequency.Instance.Read();
                // 若有每核频率，则以平均值作为 current_mhz
                int? curMHz = curMHzRaw;
                if (perCoreFreq.Length > 0)
                {
                    var vals = perCoreFreq.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                    if (vals.Length > 0)
                        curMHz = (int)Math.Round(vals.Average());
                }
                // Bus 与倍频
                var busMhz = CpuFrequency.Instance.ReadBusMhz();
                double? multiplier = null;
                if (busMhz.HasValue && busMhz.Value > 0 && curMHz.HasValue)
                {
                    multiplier = Math.Round(curMHz.Value / (double)busMhz.Value, 2);
                }
                int? minMhz = CpuFrequency.Instance.ReadMinMhz();
                // 传感器：包温度
                var pkgTempC = CpuSensors.Instance.Read();
                // 负载平均（EWMA）
                var (l1, l5, l15) = CpuLoadAverages.Instance.Update(usage);
                // Top 进程（按 CPU%）
                var top = TopProcSampler.Instance.Read(5);
                // 内核活动计数器（每秒）
                var (ctxSw, syscalls, intr) = KernelActivitySampler.Instance.Read();

                return new
                {
                    usage_percent = usage,
                    user_percent = userPct,
                    system_percent = sysPct,
                    idle_percent = idlePct,
                    uptime_sec = uptimeSec,
                    load_avg_1m = l1,
                    load_avg_5m = l5,
                    load_avg_15m = l15,
                    process_count = proc,
                    thread_count = threads,
                    per_core = perCore,
                    per_core_mhz = perCoreFreq,
                    current_mhz = curMHz,
                    max_mhz = maxMHz,
                    min_mhz = minMhz,
                    bus_mhz = busMhz,
                    multiplier = multiplier,
                    package_temp_c = pkgTempC,
                    top_processes = top,
                    context_switches_per_sec = ctxSw,
                    syscalls_per_sec = syscalls,
                    interrupts_per_sec = intr
                };
            }
        }

        private sealed class MemoryCollector : IMetricsCollector
        {
            public string Name => "memory";
            public object? Collect()
            {
                var mem = GetMemoryInfoMb();
                return new { total = mem.total_mb, used = mem.used_mb };
            }
        }

        private sealed class DiskCollector : IMetricsCollector
        {
            public string Name => "disk";
            public object? Collect()
            {
                // 使用 Windows 性能计数器（_Total）。失败则回退 0 值。
                try
                {
                    return DiskCounters.Instance.Read();
                }
                catch
                {
                    return new { read_bytes_per_sec = 0L, write_bytes_per_sec = 0L, queue_length = 0.0 };
                }
            }
        }

        private sealed class NetworkCollector : IMetricsCollector
        {
            public string Name => "network";
            public object? Collect()
            {
                // 读取所有有效网卡的 Bytes Sent/Received/sec 汇总。失败回退 0。
                try
                {
                    return NetCounters.Instance.Read();
                }
                catch
                {
                    return new { up_bytes_per_sec = 0L, down_bytes_per_sec = 0L };
                }
            }
        }

        private sealed class GpuCollector : IMetricsCollector
        {
            public string Name => "gpu";
            public object? Collect()
            {
                // 占位实现
                return null;
            }
        }

        private sealed class SensorCollector : IMetricsCollector
        {
            public string Name => "sensor";
            public object? Collect()
            {
                // 占位实现
                return null;
            }
        }

        private static readonly List<IMetricsCollector> s_collectors = new()
        {
            new CpuCollector(),
            new MemoryCollector(),
            new DiskCollector(),
            new NetworkCollector(),
            new GpuCollector(),
            new SensorCollector()
        };

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

        // -------------------------
        // Top 进程 CPU% 采样器
        // -------------------------
        private sealed class TopProcSampler
        {
            private static readonly Lazy<TopProcSampler> _inst = new(() => new TopProcSampler());
            public static TopProcSampler Instance => _inst.Value;

            private readonly object _lock = new();
            private readonly Dictionary<int, TimeSpan> _lastCpu = new();
            private long _lastTicks;
            private object[] _last = Array.Empty<object>();

            public object[] Read(int topN)
            {
                var nowTicks = Environment.TickCount64;
                // 节流：小于 800ms 返回上次结果
                lock (_lock)
                {
                    if (nowTicks - _lastTicks < 800)
                    {
                        return _last;
                    }
                }

                var sw = Stopwatch.StartNew();
                var logical = Math.Max(1, Environment.ProcessorCount);
                var snapshotAt = DateTime.UtcNow;
                var items = new List<(string name, int pid, double cpu)>();
                TimeSpan? elapsedRef = null;

                Process[] procs;
                try { procs = Process.GetProcesses(); }
                catch { procs = Array.Empty<Process>(); }

                foreach (var p in procs)
                {
                    try
                    {
                        var pid = p.Id;
                        var name = string.Empty;
                        try { name = string.IsNullOrWhiteSpace(p.ProcessName) ? "(unknown)" : p.ProcessName; } catch { name = "(unknown)"; }

                        var total = p.TotalProcessorTime; // 可能抛异常（访问拒绝/已退出）

                        TimeSpan prev;
                        lock (_lock)
                        {
                            _lastCpu.TryGetValue(pid, out prev);
                            _lastCpu[pid] = total;
                        }

                        if (!elapsedRef.HasValue)
                        {
                            // 估算采样间隔（与上次样本的 Tick 差）
                            var dtMs = Math.Max(200, nowTicks - _lastTicks);
                            elapsedRef = TimeSpan.FromMilliseconds(dtMs);
                        }

                        var delta = total - prev;
                        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
                        var elapsed = elapsedRef!.Value;
                        if (elapsed.TotalMilliseconds <= 0) continue;
                        var pct = 100.0 * (delta.TotalMilliseconds / (elapsed.TotalMilliseconds * logical));
                        pct = Math.Clamp(pct, 0.0, 100.0);
                        if (pct > 0.01)
                        {
                            items.Add((name, pid, Math.Round(pct, 2)));
                        }
                    }
                    catch
                    {
                        // ignore per-process exception
                    }
                    finally
                    {
                        try { p.Dispose(); } catch { }
                    }
                }

                var top = items
                    .OrderByDescending(i => i.cpu)
                    .ThenBy(i => i.pid)
                    .Take(Math.Max(1, topN))
                    .Select(i => (object)new { name = i.name, pid = i.pid, cpu_percent = i.cpu })
                    .ToArray();

                lock (_lock)
                {
                    _last = top;
                    _lastTicks = nowTicks;
                }
                return top;
            }
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

        // -------------------------
        // 性能计数器封装（带节流与懒加载）
        // -------------------------
        private sealed class SystemCounters
        {
            private static readonly Lazy<SystemCounters> _inst = new(() => new SystemCounters());
            public static SystemCounters Instance => _inst.Value;
            private System.Diagnostics.PerformanceCounter? _proc;
            private System.Diagnostics.PerformanceCounter? _threads;
            private long _lastTicks;
            private (int proc, int threads) _last;
            private bool _initTried;
            private void EnsureInit()
            {
                if (_initTried) return; _initTried = true;
                try
                {
                    _proc = new System.Diagnostics.PerformanceCounter("System", "Processes", readOnly: true);
                    _threads = new System.Diagnostics.PerformanceCounter("System", "Threads", readOnly: true);
                    _ = _proc.NextValue(); _ = _threads.NextValue();
                    _lastTicks = Environment.TickCount64; _last = (0, 0);
                } catch { }
            }
            public (int, int) ReadProcThread()
            {
                EnsureInit(); var now = Environment.TickCount64;
                if (now - _lastTicks < 500) return _last;
                int p = 0, t = 0; try { if (_proc != null) p = (int)_proc.NextValue(); } catch { }
                try { if (_threads != null) t = (int)_threads.NextValue(); } catch { }
                _last = (p, t); _lastTicks = now; return _last;
            }
        }

        private sealed class DiskCounters
        {
            private static readonly Lazy<DiskCounters> _inst = new(() => new DiskCounters());
            public static DiskCounters Instance => _inst.Value;

            private System.Diagnostics.PerformanceCounter? _read;
            private System.Diagnostics.PerformanceCounter? _write;
            private System.Diagnostics.PerformanceCounter? _queue;
            private long _lastTicks;
            private (long read, long write, double queue) _last;
            private bool _initTried;

            private void EnsureInit()
            {
                if (_initTried) return;
                _initTried = true;
                try
                {
                    _read = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
                    _write = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
                    _queue = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Current Disk Queue Length", "_Total", readOnly: true);
                    // 首次 NextValue 通常返回 0，允许后续采样平滑
                    _ = _read.NextValue();
                    _ = _write.NextValue();
                    _ = _queue.NextValue();
                    _lastTicks = Environment.TickCount64;
                    _last = (0, 0, 0.0);
                }
                catch
                {
                    // ignore; 使用回退
                }
            }

            public object Read()
            {
                EnsureInit();
                var now = Environment.TickCount64;
                if (now - _lastTicks < 200)
                {
                    return new { read_bytes_per_sec = _last.read, write_bytes_per_sec = _last.write, queue_length = _last.queue };
                }
                long r = 0, w = 0; double q = 0.0;
                try { if (_read != null) r = (long)_read.NextValue(); } catch { r = 0; }
                try { if (_write != null) w = (long)_write.NextValue(); } catch { w = 0; }
                try { if (_queue != null) q = _queue.NextValue(); } catch { q = 0.0; }
                _last = (r, w, q);
                _lastTicks = now;
                return new { read_bytes_per_sec = r, write_bytes_per_sec = w, queue_length = q };
            }
        }

        private sealed class NetCounters
        {
            private static readonly Lazy<NetCounters> _inst = new(() => new NetCounters());
            public static NetCounters Instance => _inst.Value;

            private System.Diagnostics.PerformanceCounter[]? _sent;
            private System.Diagnostics.PerformanceCounter[]? _recv;
            private long _lastTicks;
            private (long up, long down) _last;
            private bool _initTried;

            private static bool IsValidInterface(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return false;
                var n = name.ToLowerInvariant();
                if (n.Contains("loopback") || n.Contains("isatap") || n.Contains("teredo")) return false;
                return true;
            }

            private void EnsureInit()
            {
                if (_initTried) return;
                _initTried = true;
                try
                {
                    var cat = new System.Diagnostics.PerformanceCounterCategory("Network Interface");
                    var instances = cat.GetInstanceNames();
                    var valid = instances.Where(IsValidInterface).ToArray();
                    var sent = new List<System.Diagnostics.PerformanceCounter>();
                    var recv = new List<System.Diagnostics.PerformanceCounter>();
                    foreach (var inst in valid)
                    {
                        try
                        {
                            sent.Add(new System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Sent/sec", inst, readOnly: true));
                            recv.Add(new System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Received/sec", inst, readOnly: true));
                        }
                        catch { /* ignore this instance */ }
                    }
                    _sent = sent.ToArray();
                    _recv = recv.ToArray();
                    // 预热
                    if (_sent != null) foreach (var c in _sent) { try { _ = c.NextValue(); } catch { } }
                    if (_recv != null) foreach (var c in _recv) { try { _ = c.NextValue(); } catch { } }
                    _lastTicks = Environment.TickCount64;
                    _last = (0, 0);
                }
                catch
                {
                    // ignore
                }
            }

            public object Read()
            {
                EnsureInit();
                var now = Environment.TickCount64;
                if (now - _lastTicks < 200)
                {
                    return new { up_bytes_per_sec = _last.up, down_bytes_per_sec = _last.down };
                }
                long up = 0, down = 0;
                if (_sent != null)
                {
                    foreach (var c in _sent)
                    {
                        try { up += (long)c.NextValue(); } catch { }
                    }
                }
                if (_recv != null)
                {
                    foreach (var c in _recv)
                    {
                        try { down += (long)c.NextValue(); } catch { }
                    }
                }
                _last = (up, down);
                _lastTicks = now;
                return new { up_bytes_per_sec = up, down_bytes_per_sec = down };
            }
        }
    }
}
