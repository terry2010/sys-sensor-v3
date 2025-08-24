using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Serilog;
using System.Management;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class GpuCollector : IMetricsCollector
    {
        public string Name => "gpu";

        private static long _lastTicks;
        private static object? _lastVal;
        private static bool _initTried;

        // performance counter caches
        private static PerformanceCounterCategory? _gpuEngineCat;
        private static PerformanceCounterCategory? _gpuMemCat;

        // cached counters per instance (avoid first-read zero; reuse across calls)
        private static readonly Dictionary<string, PerformanceCounter> _enginePct = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (PerformanceCounter? dedUsed, PerformanceCounter? shaUsed, PerformanceCounter? dedLimit, PerformanceCounter? shaLimit)> _mem = new(StringComparer.OrdinalIgnoreCase);
        private static long _lastInstRefreshTicks;

        // helper: read float with safety
        private static float? SafeNext(PerformanceCounter? c)
        {
            try { return c != null ? c.NextValue() : (float?)null; } catch { return null; }
        }

        private static string SafeProcName(int pid)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                return string.IsNullOrWhiteSpace(p.ProcessName) ? $"pid {pid}" : p.ProcessName;
            }
            catch { return $"pid {pid}"; }
        }

        private static void EnsureInit()
        {
            if (_initTried) return;
            _initTried = true;
            try { _gpuEngineCat = new PerformanceCounterCategory("GPU Engine"); } catch { _gpuEngineCat = null; }
            try { _gpuMemCat = new PerformanceCounterCategory("GPU Adapter Memory"); } catch { _gpuMemCat = null; }
        }

        private static void SafePrime(PerformanceCounter? c) { try { if (c != null) _ = c.NextValue(); } catch { } }

        // ensure counters created and primed; refresh at most every 5s to catch hot-plug/driver changes
        private static void EnsureInstances()
        {
            var now = Environment.TickCount64;
            if (now - _lastInstRefreshTicks < 5000) return;
            _lastInstRefreshTicks = now;

            try
            {
                if (_gpuEngineCat != null)
                {
                    string[] instNames = Array.Empty<string>();
                    try { instNames = _gpuEngineCat.GetInstanceNames() ?? Array.Empty<string>(); } catch { instNames = Array.Empty<string>(); }
                    foreach (var inst in instNames)
                    {
                        if (!_enginePct.ContainsKey(inst))
                        {
                            try
                            {
                                var c = new PerformanceCounter("GPU Engine", "% Utilization", inst, readOnly: true);
                                SafePrime(c);
                                _enginePct[inst] = c;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (_gpuMemCat != null)
                {
                    string[] instNames = Array.Empty<string>();
                    try { instNames = _gpuMemCat.GetInstanceNames() ?? Array.Empty<string>(); } catch { instNames = Array.Empty<string>(); }
                    foreach (var inst in instNames)
                    {
                        if (!_mem.ContainsKey(inst))
                        {
                            PerformanceCounter? du = null, su = null, dl = null, sl = null;
                            try { du = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, readOnly: true); SafePrime(du); } catch { }
                            try { su = new PerformanceCounter("GPU Adapter Memory", "Shared Usage", inst, readOnly: true); SafePrime(su); } catch { }
                            try { dl = new PerformanceCounter("GPU Adapter Memory", "Dedicated Limit", inst, readOnly: true); SafePrime(dl); } catch { }
                            try { sl = new PerformanceCounter("GPU Adapter Memory", "Shared Limit", inst, readOnly: true); SafePrime(sl); } catch { }
                            _mem[inst] = (du, su, dl, sl);
                        }
                    }
                }
            }
            catch { }
        }

        public object? Collect()
        {
            var now = Environment.TickCount64;
            if (now - _lastTicks < 200 && _lastVal != null) return _lastVal;

            EnsureInit();

            try
            {
                // 1) 聚合 GPU Engine 使用率（按适配器）
                var adapters = new Dictionary<string, AdapterAgg>(StringComparer.OrdinalIgnoreCase);
                var pidAgg = new Dictionary<int, (double total, double vdec, double venc)>(capacity: 64);

                // 确保已创建并预热计数器实例
                EnsureInstances();

                if (_enginePct.Count > 0)
                {
                    foreach (var kvp in _enginePct)
                    {
                        var inst = kvp.Key; var pc = kvp.Value;
                        try
                        {
                            var key = ParseAdapterKeyFromEngineInstance(inst) ?? inst;
                            var val = SafeNext(pc) ?? 0f;
                            if (!adapters.TryGetValue(key, out var agg)) agg = new AdapterAgg(key);
                            agg.Total += val;
                            var lower = inst.ToLowerInvariant();
                            if (lower.Contains("engtype_3d")) agg.ByEng3D += val;
                            else if (lower.Contains("engtype_compute")) agg.ByEngCompute += val;
                            else if (lower.Contains("engtype_copy")) agg.ByEngCopy += val;
                            else if (lower.Contains("engtype_videodecode")) agg.ByEngVDec += val;
                            else if (lower.Contains("engtype_videoencode")) agg.ByEngVEnc += val;
                            else agg.ByEngOther += val;
                            adapters[key] = agg;

                            // 进程聚合：解析 pid
                            try
                            {
                                // 实例名惯例示例："engtype_3D_0, pid 1234, luid_0x..."
                                var parts = inst.Split(',');
                                foreach (var p in parts)
                                {
                                    var s = p.Trim();
                                    if (s.StartsWith("pid ", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var num = s.Substring(4).Trim();
                                        if (int.TryParse(num, out int pid))
                                        {
                                            var cur = pidAgg.TryGetValue(pid, out var t) ? t : (0, 0, 0);
                                            cur.total += val;
                                            if (lower.Contains("engtype_videodecode")) cur.vdec += val;
                                            if (lower.Contains("engtype_videoencode")) cur.venc += val;
                                            pidAgg[pid] = cur;
                                        }
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }
                        catch { }
                    }
                }
                // WMI 回退：当性能计数器不可用或总使用率为 0 时，使用 WMI GPUEngine 聚合 usage
                try
                {
                    bool needWmiFallback = adapters.Count == 0 || adapters.Values.All(a => a.Total <= 0.0);
                    if (needWmiFallback)
                    {
                        using var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
                        foreach (ManagementObject mo in s.Get())
                        {
                            try
                            {
                                var name = mo["Name"] as string ?? string.Empty;
                                var utilObj = mo["UtilizationPercentage"]; double util = 0;
                                if (utilObj != null) double.TryParse(utilObj.ToString(), out util);
                                if (util <= 0) continue;

                                // 按 LUID 聚合，不把 pid_...engtype_* 当成适配器行
                                var key = ParseAdapterKeyFromEngineInstance(name) ?? name;
                                if (string.IsNullOrWhiteSpace(key)) continue;
                                if (!adapters.TryGetValue(key, out var agg)) agg = new AdapterAgg(key);
                                agg.Total += util;
                                var lower = name.ToLowerInvariant();
                                if (lower.Contains("engtype_3d")) agg.ByEng3D += util;
                                else if (lower.Contains("engtype_compute")) agg.ByEngCompute += util;
                                else if (lower.Contains("engtype_copy")) agg.ByEngCopy += util;
                                else if (lower.Contains("engtype_videodecode")) agg.ByEngVDec += util;
                                else if (lower.Contains("engtype_videoencode")) agg.ByEngVEnc += util;
                                else agg.ByEngOther += util;
                                adapters[key] = agg;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                // 2) 读取 VRAM（Dedicated/Shared Usage/Limit）并尝试解析适配器显示名
                var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (_mem.Count > 0)
                {
                    foreach (var kvp in _mem)
                    {
                        var inst = kvp.Key; var ct = kvp.Value;
                        try
                        {
                            var key = ParseAdapterKeyFromMemoryInstance(inst) ?? inst;
                            string? display = null;
                            try
                            {
                                var lb = inst.IndexOf('('); var rb = inst.LastIndexOf(')');
                                if (lb >= 0 && rb > lb) display = inst.Substring(lb + 1, rb - lb - 1).Trim();
                            }
                            catch { }

                            if (!string.IsNullOrWhiteSpace(display)) nameMap[key] = display!;
                            if (!adapters.ContainsKey(key)) adapters[key] = new AdapterAgg(key);
                            var agg = adapters[key];
                            try { var v = SafeNext(ct.dedUsed); if (v.HasValue) agg.DedUsedMb = (int)Math.Max(0, Math.Round(v.Value / (1024f * 1024f))); } catch { }
                            try { var v = SafeNext(ct.shaUsed); if (v.HasValue) agg.ShaUsedMb = (int)Math.Max(0, Math.Round(v.Value / (1024f * 1024f))); } catch { }
                            try { var v = SafeNext(ct.dedLimit); if (v.HasValue) agg.DedTotalMb = (int)Math.Max(0, Math.Round(v.Value / (1024f * 1024f))); } catch { }
                            try { var v = SafeNext(ct.shaLimit); if (v.HasValue) agg.ShaTotalMb = (int)Math.Max(0, Math.Round(v.Value / (1024f * 1024f))); } catch { }
                            adapters[key] = agg;
                        }
                        catch { }
                    }
                }

                // 3) 传感器（来自 LHM）：温度、时钟、功耗、风扇、内存控制器负载
                double? gpuTemp = null;
                double? coreClockMhz = null;
                double? memClockMhz = null;
                double? packagePowerW = null;
                double? fanRpm = null;
                double? memControllerLoadPct = null;
                bool seenIntelGpu = false;
                var vendorAgg = new Dictionary<string, (double? temp, double? coreClk, double? memClk, double? powerW, double? fanRpm, double? memCtrlLoad)>(StringComparer.OrdinalIgnoreCase)
                {
                    ["nvidia"] = (null, null, null, null, null, null),
                    ["amd"] = (null, null, null, null, null, null),
                    ["intel"] = (null, null, null, null, null, null)
                };
                try
                {
                    var all = SensorsProvider.Current.DumpAll();
                    foreach (var s in all)
                    {
                        var vendor = string.Equals(s.hw_type, "GpuNvidia", StringComparison.OrdinalIgnoreCase) ? "nvidia"
                                   : string.Equals(s.hw_type, "GpuAmd", StringComparison.OrdinalIgnoreCase) ? "amd"
                                   : string.Equals(s.hw_type, "GpuIntel", StringComparison.OrdinalIgnoreCase) ? "intel" : null;
                        if (vendor == null) continue;
                        if (vendor == "intel") seenIntelGpu = true;

                        // accumulate per vendor (take max for temp; take max for clocks; sum not needed)
                        var agg = vendorAgg[vendor];
                        if (string.Equals(s.sensor_type, "Temperature", StringComparison.OrdinalIgnoreCase))
                        {
                            if (s.value.HasValue)
                            {
                                agg.temp = agg.temp.HasValue ? Math.Max(agg.temp.Value, s.value.Value) : s.value.Value;
                            }
                        }
                        else if (string.Equals(s.sensor_type, "Clock", StringComparison.OrdinalIgnoreCase))
                        {
                            if (s.value.HasValue)
                            {
                                var name = (s.sensor_name ?? string.Empty).ToLowerInvariant();
                                if (name.Contains("core")) agg.coreClk = Math.Max(agg.coreClk ?? 0, s.value.Value);
                                else if (name.Contains("mem")) agg.memClk = Math.Max(agg.memClk ?? 0, s.value.Value);
                            }
                        }
                        else if (string.Equals(s.sensor_type, "Power", StringComparison.OrdinalIgnoreCase))
                        {
                            if (s.value.HasValue)
                            {
                                var name = (s.sensor_name ?? string.Empty).ToLowerInvariant();
                                if (name.Contains("package") || name.Contains("gpu"))
                                    agg.powerW = Math.Max(agg.powerW ?? 0, s.value.Value);
                            }
                        }
                        else if (string.Equals(s.sensor_type, "Fan", StringComparison.OrdinalIgnoreCase))
                        {
                            if (s.value.HasValue)
                            {
                                agg.fanRpm = Math.Max(agg.fanRpm ?? 0, s.value.Value);
                            }
                        }
                        else if (string.Equals(s.sensor_type, "Load", StringComparison.OrdinalIgnoreCase))
                        {
                            if (s.value.HasValue)
                            {
                                var name = (s.sensor_name ?? string.Empty).ToLowerInvariant();
                                if (name.Contains("memory controller") || name.Contains("mem controller") || name.Contains("memory usage"))
                                    agg.memCtrlLoad = Math.Max(agg.memCtrlLoad ?? 0, s.value.Value);
                            }
                        }
                        vendorAgg[vendor] = agg;
                    }

                    double[] arrOf(Func<(double? temp, double? coreClk, double? memClk, double? powerW, double? fanRpm, double? memCtrlLoad), double?> sel)
                        => vendorAgg.Values.Select(sel).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                    var temps = arrOf(v => v.temp); if (temps.Length > 0) gpuTemp = temps.Max();
                    var coreClks = arrOf(v => v.coreClk); if (coreClks.Length > 0) coreClockMhz = coreClks.Max();
                    var memClks = arrOf(v => v.memClk); if (memClks.Length > 0) memClockMhz = memClks.Max();
                    var pwrs = arrOf(v => v.powerW); if (pwrs.Length > 0) packagePowerW = pwrs.Max();
                    var fans = arrOf(v => v.fanRpm); if (fans.Length > 0) fanRpm = fans.Max();
                    var mloads = arrOf(v => v.memCtrlLoad); if (mloads.Length > 0) memControllerLoadPct = mloads.Max();
                }
                catch { }

                // 回退：若 GPU 温度仍为空，尝试从所有传感器中按名称关键字匹配（覆盖 Intel iGPU 温度挂在 CPU/主板下的情况）
                try
                {
                    if (!gpuTemp.HasValue)
                    {
                        var all2 = SensorsProvider.Current.DumpAll();
                        double? found = null;
                        foreach (var s in all2)
                        {
                            if (!string.Equals(s.sensor_type, "Temperature", StringComparison.OrdinalIgnoreCase)) continue;
                            var name = (s.sensor_name ?? string.Empty).ToLowerInvariant();
                            // 常见关键词：gpu, graphics, igpu, gt（Intel 图形子系统常见前缀，如 GT0/GT1）
                            if (name.Contains("gpu") || name.Contains("graphics") || name.Contains("igpu") || name.Contains(" gt"))
                            {
                                if (s.value.HasValue)
                                    found = found.HasValue ? Math.Max(found.Value, s.value.Value) : s.value.Value;
                            }
                        }
                        if (found.HasValue) gpuTemp = found;
                    }
                }
                catch { }

                // iGPU/WMI 回退：若仅有一个适配器且名称/总量为空，尝试用 Win32_VideoController 填充
                try
                {
                    if (adapters.Count == 1)
                    {
                        var key = adapters.Keys.First();
                        var agg = adapters[key];
                        bool needName = true; // nameMap 应填充显示名
                        bool needLimits = (!agg.DedTotalMb.HasValue || agg.DedTotalMb <= 0) && (!agg.ShaTotalMb.HasValue || agg.ShaTotalMb <= 0);
                        if (needName || needLimits)
                        {
                            try
                            {
                                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
                                foreach (ManagementObject mo in searcher.Get())
                                {
                                    var name = mo["Name"] as string;
                                    var ramObj = mo["AdapterRAM"]; // bytes
                                    long ramBytes = 0; if (ramObj != null) long.TryParse(ramObj.ToString(), out ramBytes);
                                    if (needName && !string.IsNullOrWhiteSpace(name))
                                    {
                                        // 只有当 nameMap 未命中时才覆盖
                                        // 注意：无法精准映射 LUID，这里在单 GPU 情况下采用启发式
                                        // 在多 GPU 情况下不做覆盖
                                        // 设置到临时字典，稍后构造 list 时取 nameMap
                                        // 这里直接记入 nameMap
                                        // nameMap 在下方使用
                                        if (!nameMap.ContainsKey(key)) nameMap[key] = name!;
                                    }
                                    if (needLimits && ramBytes > 0)
                                    {
                                        var mb = (int)Math.Max(0, Math.Round(ramBytes / (1024.0 * 1024.0)));
                                        // 若 Dedicated 总量为 0，且 Shared 总量也缺失，则把总量暂存到 Shared 总量（更符合 iGPU 的共享内存特性）
                                        if (!agg.DedTotalMb.HasValue || agg.DedTotalMb <= 0)
                                        {
                                            agg.DedTotalMb = 0; // iGPU 常为 0
                                        }
                                        if (!agg.ShaTotalMb.HasValue || agg.ShaTotalMb <= 0)
                                        {
                                            agg.ShaTotalMb = mb;
                                        }
                                        adapters[key] = agg;
                                    }
                                    break; // 仅取第一块控制器（单 GPU 场景）
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // DXGI 显存预算（local/non-local） + 静态信息
                var dxgi = DxgiHelper.GetAdapterBudgets();
                var dxgiDesc = DxgiHelper.GetAdapterDesc1();

                // 在构造列表前：若仍未得到 GPU 温度且系统存在 Intel GPU（VendorId=0x8086），则用 CPU Package 温度兜底
                try
                {
                    if (!gpuTemp.HasValue)
                    {
                        bool hasIntel = false;
                        try { hasIntel = dxgiDesc.Values.Any(d => d.VendorId == 0x8086); } catch { }
                        if (!hasIntel) hasIntel = seenIntelGpu;
                        if (hasIntel)
                        {
                            try
                            {
                                var t = SystemMonitor.Service.Services.LhmSensors.Instance.Read().pkgTemp;
                                if (t.HasValue) gpuTemp = t;
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // 4) 构造返回对象
                var list = new List<object>();
                var indexToKey = new Dictionary<int, string>();
                int idx = 0;
                foreach (var kv in adapters.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var key = kv.Key;
                    var a = kv.Value;
                    var name = nameMap.TryGetValue(key, out var n) ? n : key;
                    double usage = Math.Clamp(a.Total, 0.0, 100.0);
                    var byEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    if (a.ByEng3D > 0) byEngine["3d"] = a.ByEng3D;
                    if (a.ByEngCompute > 0) byEngine["compute"] = a.ByEngCompute;
                    if (a.ByEngCopy > 0) byEngine["copy"] = a.ByEngCopy;
                    if (a.ByEngVDec > 0) byEngine["video_decode"] = a.ByEngVDec;
                    if (a.ByEngVEnc > 0) byEngine["video_encode"] = a.ByEngVEnc;

                    // DXGI budgets by normalized LUID
                    int? dxgiLocalBudget = null, dxgiLocalUsage = null, dxgiNonLocalBudget = null, dxgiNonLocalUsage = null;
                    // DXGI static
                    uint? vendorId = null, deviceId = null, subsysId = null, revision = null;
                    int? dxgiDedMb = null, dxgiShaMb = null;
                    uint? dxgiFlags = null;
                    try
                    {
                        var dxgiKey = NormalizeLuidKey(key);
                        if (dxgi.TryGetValue(dxgiKey, out var b))
                        {
                            dxgiLocalBudget = b.localBudgetMb;
                            dxgiLocalUsage = b.localUsageMb;
                            dxgiNonLocalBudget = b.nonlocalBudgetMb;
                            dxgiNonLocalUsage = b.nonlocalUsageMb;
                        }
                        if (dxgiDesc.TryGetValue(dxgiKey, out var d))
                        {
                            vendorId = d.VendorId;
                            deviceId = d.DeviceId;
                            subsysId = d.SubSysId;
                            revision = d.Revision;
                            dxgiDedMb = d.DedicatedVideoMemoryMb;
                            dxgiShaMb = d.SharedSystemMemoryMb;
                            dxgiFlags = d.Flags;
                        }
                    }
                    catch { }

                    list.Add(new
                    {
                        index = idx,
                        name,
                        usage_percent = usage,
                        usage_by_engine_percent = byEngine.Count > 0 ? byEngine : null,
                        vram_dedicated_used_mb = a.DedUsedMb,
                        vram_dedicated_total_mb = a.DedTotalMb ?? dxgiDedMb,
                        vram_shared_used_mb = a.ShaUsedMb,
                        vram_shared_total_mb = a.ShaTotalMb ?? dxgiShaMb,
                        dxgi_local_budget_mb = dxgiLocalBudget,
                        dxgi_local_usage_mb = dxgiLocalUsage,
                        dxgi_nonlocal_budget_mb = dxgiNonLocalBudget,
                        dxgi_nonlocal_usage_mb = dxgiNonLocalUsage,
                        dxgi_flags = dxgiFlags,
                        vendor_id = vendorId,
                        device_id = deviceId,
                        subsys_id = subsysId,
                        revision = revision,
                        core_clock_mhz = coreClockMhz,
                        memory_clock_mhz = memClockMhz,
                        power_draw_w = packagePowerW,
                        fan_rpm = fanRpm,
                        mem_controller_load_percent = memControllerLoadPct,
                        video_encode_util_percent = a.ByEngVEnc > 0 ? a.ByEngVEnc : (double?)null,
                        video_decode_util_percent = a.ByEngVDec > 0 ? a.ByEngVDec : (double?)null,
                        temperature_core_c = gpuTemp
                    });
                    indexToKey[idx] = key;
                    idx++;
                }

                // 活跃适配器（启发式：优先选择“真实适配器”而非引擎/进程行）
                int? activeIdx = null; string? activeName = null;
                try
                {
                    if (list.Count > 0)
                    {
                        var arr = list.Select((o, i) => new { obj = o, i }).ToArray();
                        // 标记真实适配器：名称含 luid_ 或 有任一显存字段或有厂商ID
                        bool IsReal(object o)
                        {
                            try
                            {
                                var name = (string?)o_get(o, "name") ?? string.Empty;
                                if (name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                                if (o_get(o, "vendor_id") != null) return true;
                                if (o_get(o, "vram_dedicated_used_mb") != null) return true;
                                if (o_get(o, "vram_shared_used_mb") != null) return true;
                                return false;
                            }
                            catch { return false; }
                        }

                        var real = arr.Where(x => IsReal(x.obj)).ToArray();
                        var pickFrom = real.Length > 0 ? real : arr;
                        var max = pickFrom
                            .Select(x => new { x.i, usage = (double?)o_get(x.obj, "usage_percent"), name = (string?)o_get(x.obj, "name") })
                            .OrderByDescending(x => x.usage ?? -1)
                            .FirstOrDefault();
                        if (max != null && (max.usage ?? 0) > 5.0)
                        {
                            activeIdx = max.i;
                            activeName = max.name;
                        }
                        else
                        {
                            // 回退：选择第一个适配器
                            var first = pickFrom.FirstOrDefault();
                            if (first != null)
                            {
                                activeIdx = first.i;
                                activeName = (string?)o_get(first.obj, "name");
                            }
                            else
                            {
                                activeIdx = 0;
                                activeName = (string?)o_get(list[0], "name");
                            }
                        }
                    }
                }
                catch { }

                // Top 进程：取前 5（合并所有适配器），按 total desc
                object? topProcs = null;
                try
                {
                    if (pidAgg.Count > 0)
                    {
                        var items = pidAgg
                            .Select(kv => new { pid = kv.Key, kv.Value.total, kv.Value.vdec, kv.Value.venc })
                            .OrderByDescending(x => x.total)
                            .Take(5)
                            .Select(x => new
                            {
                                pid = x.pid,
                                name = SafeProcName(x.pid),
                                usage_percent = Math.Clamp(x.total, 0.0, 100.0),
                                video_decode_util_percent = x.vdec > 0 ? x.vdec : (double?)null,
                                video_encode_util_percent = x.venc > 0 ? x.venc : (double?)null
                            })
                            .ToArray();
                        if (items.Length > 0) topProcs = items;
                    }
                }
                catch { }

                // WMI 回退：Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine
                // 某些会话/驱动下性能计数器实例不可见，但 WMI 格式化类可用
                try
                {
                    if (topProcs == null)
                    {
                        var wmiAgg = new Dictionary<int, (double total, double vdec, double venc)>();
                        using var s = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
                        foreach (ManagementObject mo in s.Get())
                        {
                            try
                            {
                                var name = mo["Name"] as string ?? string.Empty;
                                var utilObj = mo["UtilizationPercentage"]; double util = 0;
                                if (utilObj != null) double.TryParse(utilObj.ToString(), out util);
                                if (util <= 0) continue;
                                // 解析 pid：兼容 "pid 1234" 或 "pid_1234"
                                int pid = -1;
                                foreach (var part in name.Split(','))
                                {
                                    var t = part.Trim();
                                    if (t.StartsWith("pid ", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var num = t.Substring(4).Trim();
                                        if (int.TryParse(num, out var p)) { pid = p; break; }
                                    }
                                    else if (t.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var num = t.Substring(4).Trim();
                                        if (int.TryParse(num, out var p)) { pid = p; break; }
                                    }
                                }
                                if (pid <= 0) continue;
                                var lower = name.ToLowerInvariant();
                                var cur = wmiAgg.TryGetValue(pid, out var v) ? v : (0, 0, 0);
                                cur.total += util;
                                if (lower.Contains("engtype_videodecode")) cur.vdec += util;
                                if (lower.Contains("engtype_videoencode")) cur.venc += util;
                                wmiAgg[pid] = cur;
                            }
                            catch { }
                        }
                        if (wmiAgg.Count > 0)
                        {
                            var items = wmiAgg
                                .Select(kv => new { pid = kv.Key, kv.Value.total, kv.Value.vdec, kv.Value.venc })
                                .OrderByDescending(x => x.total)
                                .Take(5)
                                .Select(x => new
                                {
                                    pid = x.pid,
                                    name = SafeProcName(x.pid),
                                    usage_percent = Math.Clamp(x.total, 0.0, 100.0),
                                    video_decode_util_percent = x.vdec > 0 ? x.vdec : (double?)null,
                                    video_encode_util_percent = x.venc > 0 ? x.venc : (double?)null
                                })
                                .ToArray();
                            if (items.Length > 0) topProcs = items;
                        }
                    }
                }
                catch { }

                // 从 WMI 拿到驱动信息（优先真实 GPU：匹配 DXGI VendorId 的 PCI 设备；排除 ROOT\\DISPLAY/虚拟/镜像驱动）
                object? activeStatic = null;
                try
                {
                    if (activeName != null)
                    {
                        using var searcher = new System.Management.ManagementObjectSearcher("root\\CIMV2", "SELECT Name,PNPDeviceID,DriverVersion,DriverDate,AdapterCompatibility FROM Win32_VideoController");
                        System.Management.ManagementObject? chosen = null;
                        string? activeKey = null;
                        uint? activeVendor = null;
                        try
                        {
                            if (activeIdx.HasValue && indexToKey.TryGetValue(activeIdx.Value, out var k1))
                            {
                                activeKey = k1;
                                var dxgiKey = NormalizeLuidKey(k1);
                                if (dxgiDesc.TryGetValue(dxgiKey, out var d)) activeVendor = d.VendorId;
                            }
                        }
                        catch { }
                        string? preferredVen = null;
                        if (activeVendor.HasValue)
                        {
                            preferredVen = "VEN_" + activeVendor.Value.ToString("X4"); // 10DE/NVIDIA, 1002/AMD, 8086/INTEL
                        }

                        bool IsVirtualOrMirror(string? name, string? compat, string? pnp)
                        {
                            string s(string? x) => x?.ToLowerInvariant() ?? string.Empty;
                            var n = s(name); var c = s(compat); var p = s(pnp);
                            if (p.StartsWith("root\\display")) return true;
                            if (n.Contains("idd") || n.Contains("oray") || n.Contains("mirror") || n.Contains("displaylink") || n.Contains("rdp") || n.Contains("basic display")) return true;
                            if (c.Contains("oray") || c.Contains("microsoft basic display") || c.Contains("rdp") || c.Contains("mirror")) return true;
                            return false;
                        }

                        // 第一轮：严格匹配 Vendor 且是 PCI 设备
                        foreach (System.Management.ManagementObject obj in searcher.Get())
                        {
                            string? wmiName = obj["Name"] as string;
                            string? pnp = obj["PNPDeviceID"] as string;
                            string? compat = obj["AdapterCompatibility"] as string;
                            if (string.IsNullOrWhiteSpace(wmiName)) continue;
                            if (IsVirtualOrMirror(wmiName, compat, pnp)) continue;
                            if (!string.IsNullOrEmpty(preferredVen))
                            {
                                if (!string.IsNullOrEmpty(pnp) && pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) && pnp.IndexOf(preferredVen, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    chosen = obj; activeName = wmiName; break;
                                }
                            }
                        }
                        // 第二轮：放宽到“Intel/NVIDIA/AMD”等兼容名，仍要求非虚拟
                        if (chosen == null)
                        {
                            foreach (System.Management.ManagementObject obj in searcher.Get())
                            {
                                string? wmiName = obj["Name"] as string;
                                string? compat = obj["AdapterCompatibility"] as string;
                                string? pnp = obj["PNPDeviceID"] as string;
                                if (string.IsNullOrWhiteSpace(wmiName)) continue;
                                if (IsVirtualOrMirror(wmiName, compat, pnp)) continue;
                                var c = compat?.ToLowerInvariant() ?? string.Empty;
                                if (c.Contains("intel") || c.Contains("nvidia") || c.Contains("advanced micro devices") || c.Contains("amd"))
                                { chosen = obj; activeName = wmiName; break; }
                            }
                        }
                        // 第三轮：仍然找不到，则回退第一条（最后兜底）
                        if (chosen == null)
                        {
                            foreach (System.Management.ManagementObject obj in searcher.Get()) { chosen = obj; break; }
                            if (chosen != null)
                            {
                                var wmiName = chosen["Name"] as string; if (!string.IsNullOrWhiteSpace(wmiName)) activeName = wmiName;
                            }
                        }

                        if (chosen != null)
                        {
                            string? rawDate = chosen["DriverDate"] as string;
                            string? normDate = NormalizeWmiDate(rawDate);
                            activeStatic = new
                            {
                                name = activeName,
                                driver_version = chosen["DriverVersion"] as string,
                                driver_date = normDate,
                                pnp_device_id = chosen["PNPDeviceID"] as string,
                                adapter_compatibility = chosen["AdapterCompatibility"] as string
                            };
                            // 若能定位到 active 的 key，则用 WMI 名称覆盖该适配器的显示名，优化表格展示
                            try
                            {
                                if (activeIdx.HasValue && indexToKey.TryGetValue(activeIdx.Value, out var k2))
                                {
                                    var wname = chosen["Name"] as string;
                                    if (!string.IsNullOrWhiteSpace(wname)) nameMap[k2] = wname!;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                var result = new
                {
                    adapters = list.Count > 0 ? list : null,
                    active_adapter_index = activeIdx,
                    active_adapter_name = activeName,
                    top_processes = topProcs,
                    active_adapter_static = activeStatic
                };

                // 调试：若全部使用率为 0 且内存上限缺失，输出实例名帮助诊断（日志输出到 logs/service-*.log）
                try
                {
                    bool allZero = list.Count > 0 && list.All(o => ((double?)o_get(o, "usage_percent") ?? 0.0) <= 0.01);
                    bool memMissing = list.Count > 0 && list.All(o => (int?)o_get(o, "vram_dedicated_total_mb") == null && (int?)o_get(o, "vram_shared_total_mb") == null);
                    if (allZero || memMissing)
                    {
                        string eng = string.Join("; ", _enginePct.Keys.Take(20));
                        string mem = string.Join("; ", _mem.Keys.Take(20));
                        Log.Information("[GpuCollector] Debug iGPU: engineInsts={Engine} memInsts={Mem}", eng, mem);
                    }
                }
                catch { }

                _lastVal = result; _lastTicks = now;
                return _lastVal = result;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GpuCollector.Collect error");
                return _lastVal ?? new { adapters = (object?)null };
            }
        }

        // WMI CIM_DATETIME -> yyyy-MM-dd
        private static string? NormalizeWmiDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            try
            {
                // Expected: yyyymmddHHMMSS.mmmmmm±UUU
                if (s!.Length >= 8)
                {
                    var y = s.Substring(0, 4);
                    var m = s.Substring(4, 2);
                    var d = s.Substring(6, 2);
                    if (int.TryParse(y, out var yy) && int.TryParse(m, out var mm) && int.TryParse(d, out var dd))
                    {
                        return new DateTime(Math.Clamp(yy, 1980, 9999), Math.Clamp(mm, 1, 12), Math.Clamp(dd, 1, 28)).ToString("yyyy-MM-dd");
                    }
                }
            }
            catch { }
            return s;
        }

        private static object? o_get(object obj, string prop)
        {
            try { return obj.GetType().GetProperty(prop)?.GetValue(obj); } catch { return null; }
        }

        private static string NormalizeLuidKey(string k)
        {
            try
            {
                var s = k;
                var idx = s.LastIndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
                if (idx > 0) s = s.Substring(0, idx);
                return s;
            }
            catch { return k; }
        }

        private static string? ParseAdapterKeyFromEngineInstance(string inst)
        {
            // 典型："engtype_3D_0, pid 1234, luid_0x000..._0x000"
            try
            {
                // 先直接在整串里查找 "luid_"
                int i = inst.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                {
                    // 结束位置：优先空格/逗号/括号，否则到结尾
                    int end = inst.Length;
                    int sp = inst.IndexOf(' ', i);
                    if (sp >= 0) end = Math.Min(end, sp);
                    int cm = inst.IndexOf(',', i);
                    if (cm >= 0) end = Math.Min(end, cm);
                    int rb = inst.IndexOf(')', i);
                    if (rb >= 0) end = Math.Min(end, rb);
                    var key = inst.Substring(i, end - i).Trim();
                    return NormalizeLuidKey(key);
                }

                // 回退：按逗号/空格分割再找 token
                var parts = inst.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var luid = parts.FirstOrDefault(p => p.StartsWith("luid_", StringComparison.OrdinalIgnoreCase));
                var key2 = string.IsNullOrWhiteSpace(luid) ? inst : luid;
                return NormalizeLuidKey(key2);
            }
            catch { return inst; }
        }

        private static string? ParseAdapterKeyFromMemoryInstance(string inst)
        {
            // 典型："luid_0x000..._0x000 (NVIDIA GeForce RTX ...)"
            try
            {
                var i = inst.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                {
                    var end = inst.IndexOf(' ', i);
                    if (end < 0) end = inst.IndexOf('(', i);
                    if (end < 0) end = inst.Length;
                    var key = inst.Substring(i, end - i).Trim();
                    return NormalizeLuidKey(key);
                }
                return inst;
            }
            catch { return inst; }
        }

        private static (string? displayName, int? dedUsedMb, int? dedLimitMb, int? shaUsedMb, int? shaLimitMb) ReadAdapterMemory(string inst)
        {
            string? display = null;
            try
            {
                var lb = inst.IndexOf('(');
                var rb = inst.LastIndexOf(')');
                if (lb >= 0 && rb > lb) display = inst.Substring(lb + 1, rb - lb - 1).Trim();
            }
            catch { }

            int? du = null, dl = null, su = null, sl = null;
            try { du = (int?)Math.Max(0, Math.Round((SafeNext(new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, readOnly: true)) ?? 0f) / (1024f * 1024f))); } catch { }
            try { su = (int?)Math.Max(0, Math.Round((SafeNext(new PerformanceCounter("GPU Adapter Memory", "Shared Usage", inst, readOnly: true)) ?? 0f) / (1024f * 1024f))); } catch { }
            try { dl = (int?)Math.Max(0, Math.Round((SafeNext(new PerformanceCounter("GPU Adapter Memory", "Dedicated Limit", inst, readOnly: true)) ?? 0f) / (1024f * 1024f))); } catch { }
            try { sl = (int?)Math.Max(0, Math.Round((SafeNext(new PerformanceCounter("GPU Adapter Memory", "Shared Limit", inst, readOnly: true)) ?? 0f) / (1024f * 1024f))); } catch { }
            return (display, du, dl, su, sl);
        }

        private sealed class AdapterAgg
        {
            public string Key { get; }
            public double Total;
            public double ByEng3D;
            public double ByEngCompute;
            public double ByEngCopy;
            public double ByEngVDec;
            public double ByEngVEnc;
            public double ByEngOther;
            public int? DedUsedMb;
            public int? DedTotalMb;
            public int? ShaUsedMb;
            public int? ShaTotalMb;
            public AdapterAgg(string key) { Key = key; }
        }
    }
}
