using System.Linq;
using System.Collections.Generic;
using SystemMonitor.Service.Services;
namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class DiskCollector : IMetricsCollector
    {
        public string Name => "disk";
        // 慢路径缓存（物理盘/SMART/NVMe Identify/ErrorLog）——降低每轮重 I/O 频率
        private static readonly object _slowLock = new object();
        private static int PHYS_TTL_MS = 30000;       // 物理盘 ≥30s 刷新（从原来的10s增加到30s）
        private static int SMART_TTL_MS = 30000;      // SMART/温度 ≥30s 刷新（可配置）
        private static int NVME_ERRLOG_TTL_MS = 90000; // NVMe ErrorLog ≥90s 刷新（可配置）
        private static int NVME_IDENT_TTL_MS = 600000; // NVMe Identify/Namespace ≥10min 刷新（可配置）
        private static bool? _smartNativeEnabledOverride = null; // 运行时覆盖；null 表示按环境变量判定
        private static long _physAtMs = 0;
        private static System.Collections.Generic.IReadOnlyList<SystemMonitor.Service.Services.Collectors.StorageQuery.PhysicalDiskInfo>? _physCache = null;
        private static long _smartAtMs = 0;
        private static object[]? _smartCache = null;

        // NVMe 按盘缓存（索引 -> 缓存项）
        private static readonly object _nvmeLock = new object();
        private static readonly System.Collections.Generic.Dictionary<int, (long atMs, (string sn, string mn, string fr)? data)> _nvmeIdentifyStrCache = new();
        private static readonly System.Collections.Generic.Dictionary<int, (long atMs, (uint bestNsid, int lbaBytes, ulong nsze, ulong nuse, string? eui64, string? nguid, uint? nn)?)> _nvmeNsInfoCache = new();
        private static readonly System.Collections.Generic.Dictionary<int, (long atMs, object? obj)> _nvmeErrorLogCache = new();

        // 后台预热入口（供服务在后台定期调用）
        public static void RefreshPhysicalCache()
        {
            try { _ = GetPhysicalDisksCached(forceRefresh: true); } catch { }
        }
        // 运行时应用配置（由 RpcServer.set_config 调用）
        public static void ApplyConfig(int? smartTtlMs, int? nvmeErrlogTtlMs, int? nvmeIdentTtlMs, bool? smartNativeEnabled)
        {
            lock (_slowLock)
            {
                if (smartTtlMs.HasValue)
                {
                    var v = System.Math.Clamp(smartTtlMs.Value, 5000, 300000); // 5s..5min
                    SMART_TTL_MS = v;
                }
                if (nvmeErrlogTtlMs.HasValue)
                {
                    var v = System.Math.Clamp(nvmeErrlogTtlMs.Value, 10000, 600000); // 10s..10min
                    NVME_ERRLOG_TTL_MS = v;
                }
                if (nvmeIdentTtlMs.HasValue)
                {
                    var v = System.Math.Clamp(nvmeIdentTtlMs.Value, 60000, 3600000); // 1min..60min
                    NVME_IDENT_TTL_MS = v;
                }
                if (smartNativeEnabled.HasValue)
                {
                    _smartNativeEnabledOverride = smartNativeEnabled.Value;
                }
            }
        }

        // 读取当前磁盘运行时配置（供 get_config 使用）
        public static (int smartTtlMs, int nvmeErrlogTtlMs, int nvmeIdentTtlMs, bool? smartNativeOverride, bool smartNativeEffective) ReadRuntimeConfig()
        {
            lock (_slowLock)
            {
                bool effective;
                if (_smartNativeEnabledOverride.HasValue)
                {
                    effective = _smartNativeEnabledOverride.Value;
                }
                else
                {
                    // 环境变量：1 表示禁用原生 SMART
                    var dis = string.Equals(System.Environment.GetEnvironmentVariable("SYS_SENSOR_DISABLE_NATIVE_SMART"), "1", System.StringComparison.OrdinalIgnoreCase);
                    effective = !dis;
                }
                return (SMART_TTL_MS, NVME_ERRLOG_TTL_MS, NVME_IDENT_TTL_MS, _smartNativeEnabledOverride, effective);
            }
        }

        private static System.Collections.Generic.IReadOnlyList<SystemMonitor.Service.Services.Collectors.StorageQuery.PhysicalDiskInfo>? GetPhysicalDisksCached(bool forceRefresh = false)
        {
            var now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_slowLock)
            {
                if (!forceRefresh && _physCache != null && (now - _physAtMs) < PHYS_TTL_MS)
                {
                    return _physCache;
                }
            }
            try
            {
                var fresh = StorageQuery.Instance.ReadPhysicalDisks();
                lock (_slowLock) { _physCache = fresh; _physAtMs = now; }
                return fresh;
            }
            catch
            {
                lock (_slowLock) { return _physCache; }
            }
        }
        public object? Collect()
        {
            try
            {
                // 兼容旧字段（顶层）
                var basic = DiskCounters.Instance.Read();
                // 扩展 totals 与 per-instance
                var totalsRaw = DiskCounters.Instance.ReadTotals();
                var perInst = DiskCounters.Instance.ReadPerInstance();
                var perVol = LogicalDiskCounters.Instance.ReadPerVolume();
                // 阶段B：容量与静态信息
                var (vols, capTotals) = StorageQuery.Instance.ReadVolumes();
                var vmSwapBytes = StorageQuery.Instance.ReadVmSwapfilesBytes();
                // 物理盘信息走缓存（≥10s）
                var phys = GetPhysicalDisksCached();

                // 反射取值的小工具
                static long GetLong(object obj, string name)
                {
                    var p = obj.GetType().GetProperty(name); if (p == null) return 0L;
                    var v = p.GetValue(obj); if (v == null) return 0L;
                    try { return System.Convert.ToInt64(v); } catch { return 0L; }
                }
                static double? GetDoubleN(object obj, string name)
                {
                    var p = obj.GetType().GetProperty(name); if (p == null) return null;
                    var v = p.GetValue(obj); if (v == null) return null;
                    try { return System.Convert.ToDouble(v); } catch { return null; }
                }
                static string? GetStringN(object obj, string name)
                {
                    var p = obj.GetType().GetProperty(name); if (p == null) return null;
                    var v = p.GetValue(obj); if (v == null) return null;
                    try { return System.Convert.ToString(v); } catch { return null; }
                }

                // 读取 totals 原始值
                var totalsRead = GetLong(totalsRaw, "read_bytes_per_sec");
                var totalsWrite = GetLong(totalsRaw, "write_bytes_per_sec");
                // 更新 totals 的延迟分位数聚合（以 IOPS 为权重）
                var totReadIops = GetDoubleN(totalsRaw, "read_iops");
                var totWriteIops = GetDoubleN(totalsRaw, "write_iops");
                var totReadLat = GetDoubleN(totalsRaw, "avg_read_latency_ms");
                var totWriteLat = GetDoubleN(totalsRaw, "avg_write_latency_ms");
                LatencyAggregator.Instance.Update("_totals", totReadLat, totWriteLat, totReadIops, totWriteIops);

                // 优先使用更细粒度的聚合，以避免系统 totals 计数器在部分环境失真
                long sumVolRead = 0, sumVolWrite = 0; bool hasVol = false;
                if (perVol != null)
                {
                    foreach (var v in perVol)
                    {
                        sumVolRead += GetLong(v, "read_bytes_per_sec");
                        sumVolWrite += GetLong(v, "write_bytes_per_sec");
                        hasVol = true;
                    }
                }
                long sumInstRead = 0, sumInstWrite = 0; bool hasInst = false;
                if (perInst != null)
                {
                    foreach (var d in perInst)
                    {
                        sumInstRead += GetLong(d, "read_bytes_per_sec");
                        sumInstWrite += GetLong(d, "write_bytes_per_sec");
                        hasInst = true;
                    }
                }

                long finalRead = totalsRead;
                long finalWrite = totalsWrite;
                string totalsSource = "totals_raw";
                if (hasVol)
                {
                    finalRead = sumVolRead;
                    finalWrite = sumVolWrite;
                    totalsSource = "per_volume";
                }
                else if (hasInst)
                {
                    finalRead = sumInstRead;
                    finalWrite = sumInstWrite;
                    totalsSource = "per_physical";
                }

                // 生成对外 totals（保持字段齐全）
                var (tot_rp50, tot_rp95, tot_rp99, tot_wp50, tot_wp95, tot_wp99) = LatencyAggregator.Instance.GetPercentiles("_totals");
                var totals = new
                {
                    read_bytes_per_sec = finalRead,
                    write_bytes_per_sec = finalWrite,
                    read_iops = GetDoubleN(totalsRaw, "read_iops"),
                    write_iops = GetDoubleN(totalsRaw, "write_iops"),
                    busy_percent = GetDoubleN(totalsRaw, "busy_percent"),
                    queue_length = GetDoubleN(totalsRaw, "queue_length"),
                    avg_read_latency_ms = GetDoubleN(totalsRaw, "avg_read_latency_ms"),
                    avg_write_latency_ms = GetDoubleN(totalsRaw, "avg_write_latency_ms"),
                    // 新增：滑窗分位数
                    read_p50_ms = tot_rp50,
                    read_p95_ms = tot_rp95,
                    read_p99_ms = tot_rp99,
                    write_p50_ms = tot_wp50,
                    write_p95_ms = tot_wp95,
                    write_p99_ms = tot_wp99
                };

                // enrich per_volume_io with free_percent from volumes + 分位数聚合
                System.Collections.Generic.List<object>? perVolEnriched = null;
                if (perVol != null)
                {
                    perVolEnriched = new System.Collections.Generic.List<object>();
                    foreach (var v in perVol)
                    {
                        var vid = GetStringN(v, "volume_id");
                        // 更新该卷的滑窗聚合
                        var rIops = GetDoubleN(v, "read_iops");
                        var wIops = GetDoubleN(v, "write_iops");
                        var rLat = GetDoubleN(v, "avg_read_latency_ms");
                        var wLat = GetDoubleN(v, "avg_write_latency_ms");
                        if (!string.IsNullOrWhiteSpace(vid))
                        {
                            LatencyAggregator.Instance.Update($"vol:{vid}", rLat, wLat, rIops, wIops);
                        }
                        var (rp50, rp95, rp99, wp50, wp95, wp99) = string.IsNullOrWhiteSpace(vid) ? (null, null, null, null, null, null) : LatencyAggregator.Instance.GetPercentiles($"vol:{vid}");
                        // 容量 free%
                        double? freePct = null;
                        try
                        {
                            if (vols != null)
                            {
                                var match = vols.FirstOrDefault(x => string.Equals(x.id, vid, System.StringComparison.OrdinalIgnoreCase));
                                freePct = match?.free_percent;
                            }
                        }
                        catch { }
                        perVolEnriched.Add(new
                        {
                            volume_id = vid ?? string.Empty,
                            read_bytes_per_sec = GetLong(v, "read_bytes_per_sec"),
                            write_bytes_per_sec = GetLong(v, "write_bytes_per_sec"),
                            read_iops = rIops,
                            write_iops = wIops,
                            busy_percent = GetDoubleN(v, "busy_percent"),
                            queue_length = GetDoubleN(v, "queue_length"),
                            avg_read_latency_ms = rLat,
                            avg_write_latency_ms = wLat,
                            // 新增：分位数
                            read_p50_ms = rp50,
                            read_p95_ms = rp95,
                            read_p99_ms = rp99,
                            write_p50_ms = wp50,
                            write_p95_ms = wp95,
                            write_p99_ms = wp99,
                            free_percent = freePct
                        });
                    }
                }

                // 兜底：若物理盘静态信息为空，则基于 per_physical_disk_io 的实例名生成最小占位列表
                object? physOut = phys;
                try
                {
                    if ((phys == null || phys.Count == 0) && perInst != null)
                    {
                        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                        var fallback = new System.Collections.Generic.List<object>();
                        foreach (var d in perInst)
                        {
                            var id = GetStringN(d, "disk_id") ?? GetStringN(d, "device_id") ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(id)) continue;
                            if (seen.Add(id))
                            {
                                fallback.Add(new
                                {
                                    id = id,
                                    model = (string?)null,
                                    serial = (string?)null,
                                    firmware = (string?)null,
                                    size_total_bytes = (long?)null,
                                    partitions = (int?)null,
                                    media_type = (string?)null,
                                    spindle_speed_rpm = (int?)null,
                                    interface_type = (string?)null,
                                    trim_supported = (bool?)null,
                                    bus_type = (string?)null,
                                    negotiated_link_speed = (string?)null,
                                    is_removable = (bool?)null,
                                    eject_capable = (bool?)null
                                });
                            }
                        }
                        if (fallback.Count > 0) physOut = fallback;
                    }
                }
                catch { }

                // smart/温度：阶段B线（优先原生 IOCTL），阶段A回退（LHM 模糊匹配）
                object[]? smartHealth = null;
                var nowMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                bool smartFromCache = false;
                lock (_slowLock)
                {
                    if (_smartCache != null && (nowMs - _smartAtMs) < SMART_TTL_MS)
                    {
                        smartHealth = _smartCache;
                        smartFromCache = true;
                    }
                }
                try
                {
                    var physList = physOut as IEnumerable<object>;
                    if (!smartFromCache && physList != null)
                    {
                        // 环境变量开关
                        bool disableNative;
                        if (_smartNativeEnabledOverride.HasValue)
                        {
                            disableNative = !_smartNativeEnabledOverride.Value;
                        }
                        else
                        {
                            // 兼容旧环境变量：1=禁用
                            disableNative = string.Equals(System.Environment.GetEnvironmentVariable("SYS_SENSOR_DISABLE_NATIVE_SMART"), "1", System.StringComparison.OrdinalIgnoreCase);
                        }
                        bool smartDebug = string.Equals(System.Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", System.StringComparison.OrdinalIgnoreCase);

                        var lhm = SensorsProvider.Current.DumpAll();
                        // 将 Storage 硬件的传感器按硬件名分组，便于按型号模糊匹配后查找具体指标
                        var storageGroups = lhm
                            .Where(s => string.Equals(s.hw_type, "Storage", System.StringComparison.OrdinalIgnoreCase) && s.hw_name != null)
                            .GroupBy(s => s.hw_name!)
                            .ToDictionary(g => g.Key, g => g.ToList(), System.StringComparer.OrdinalIgnoreCase);

                        // 便捷函数：在一个硬件组里按名称子串查找第一个数值
                        static double? FindVal(IEnumerable<SystemMonitor.Service.Services.LhmSensorDto> list, params string[] nameContains)
                        {
                            foreach (var key in nameContains)
                            {
                                var hit = list.FirstOrDefault(x => (x.sensor_name ?? string.Empty).IndexOf(key, System.StringComparison.OrdinalIgnoreCase) >= 0 && x.value.HasValue);
                                if (hit != null) return hit.value;
                            }
                            return null;
                        }

                        var outList = new List<object>();
                        // 提取物理盘索引工具
                        static int? TryExtractIndex(string? id)
                        {
                            if (string.IsNullOrWhiteSpace(id)) return null;
                            try
                            {
                                // 常见形式："\\\\.\\PHYSICALDRIVE0" 或 "PhysicalDrive1" 或 "0"
                                var s = new string(id.Where(char.IsDigit).ToArray());
                                if (int.TryParse(s, out var v)) return v;
                            }
                            catch { }
                            return null;
                        }
                        foreach (var p in physList)
                        {
                            var id = GetStringN(p, "id") ?? string.Empty;
                            var model = GetStringN(p, "model") ?? string.Empty;
                            var serial = GetStringN(p, "serial") ?? string.Empty;
                            var iface = GetStringN(p, "interface_type") ?? GetStringN(p, "bus_type") ?? string.Empty;
                            string? matchedHw = null;
                            List<SystemMonitor.Service.Services.LhmSensorDto>? sensors = null;
                            // 1) 优先用序列号匹配（部分厂牌 LHM 硬件名中包含序列号）
                            if (!string.IsNullOrWhiteSpace(serial))
                            {
                                var m = storageGroups.Keys.FirstOrDefault(n => n.IndexOf(serial, System.StringComparison.OrdinalIgnoreCase) >= 0);
                                if (!string.IsNullOrEmpty(m)) { matchedHw = m; sensors = storageGroups[m]; }
                            }
                            // 2) 其次用型号匹配
                            if (sensors == null && !string.IsNullOrWhiteSpace(model))
                            {
                                // 找到第一个硬件名包含型号字符串的组
                                var m = storageGroups.Keys.FirstOrDefault(n => n.IndexOf(model, System.StringComparison.OrdinalIgnoreCase) >= 0);
                                if (!string.IsNullOrEmpty(m)) { matchedHw = m; sensors = storageGroups[m]; }
                            }
                            // 3) 兜底：用 id（例如包含 PhysicalDriveN / NVMe 等）
                            if (sensors == null && !string.IsNullOrWhiteSpace(id))
                            {
                                var m = storageGroups.Keys.FirstOrDefault(n => n.IndexOf(id, System.StringComparison.OrdinalIgnoreCase) >= 0);
                                if (!string.IsNullOrEmpty(m)) { matchedHw = m; sensors = storageGroups[m]; }
                            }

                            // 若仍未匹配，且判定为 NVMe，或者 interface_type 为空不确定，也尝试通过 Identify 获取 SN/MN 再匹配（带缓存）
                            int? idxForNvme = TryExtractIndex(id);
                            if (sensors == null && idxForNvme.HasValue &&
                                (((!string.IsNullOrWhiteSpace(iface)) && iface.IndexOf("nvme", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                  || string.IsNullOrWhiteSpace(iface)))
                            {
                                try
                                {
                                    var idf = TryReadNvmeIdentifyStringsCached(idxForNvme.Value);
                                    if (idf.HasValue)
                                    {
                                        var (sn2, mn2, fr2) = idf.Value;
                                        if (!string.IsNullOrWhiteSpace(sn2))
                                        {
                                            var m = storageGroups.Keys.FirstOrDefault(n => n.IndexOf(sn2, System.StringComparison.OrdinalIgnoreCase) >= 0);
                                            if (!string.IsNullOrEmpty(m)) { matchedHw = m; sensors = storageGroups[m]; }
                                        }
                                        if (sensors == null && !string.IsNullOrWhiteSpace(mn2))
                                        {
                                            var m = storageGroups.Keys.FirstOrDefault(n => n.IndexOf(mn2, System.StringComparison.OrdinalIgnoreCase) >= 0);
                                            if (!string.IsNullOrEmpty(m)) { matchedHw = m; sensors = storageGroups[m]; }
                                        }
                                        if (smartDebug)
                                        {
                                            try { Serilog.Log.Information("[SMART] NVMe Identify idx={Idx} SN={SN} MN={MN} FR={FR} matchedHw={Matched}", idxForNvme, sn2, mn2, fr2, matchedHw); } catch { }
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (smartDebug)
                            {
                                try { Serilog.Log.Information("[SMART] match disk id={Id} iface={Iface} serial={Serial} model={Model} => matchedHw={Matched}", id, iface, serial, model, matchedHw); } catch { }
                            }

                            double? temperature = null;
                            double? pwrOnHours = null;
                            double? realloc = null;
                            double? pending = null;
                            double? udmaCrc = null;
                            double? nvmePctUsed = null;
                            double? dataRead = null;
                            double? dataWritten = null;
                            double? ctrlBusyMin = null;
                            double? unsafeShutdowns = null;
                            double? throttleEvents = null;
                            // 新增 NVMe 字段
                            double? nvmePowerCycles = null;
                            double? nvmePoh = null;
                            double? nvmeMediaErrors = null;
                            double? nvmeAvailSpare = null;
                            double? nvmeSpareThreshold = null;
                            byte? nvmeCriticalWarning = null;
                            double? nvmeTs1 = null, nvmeTs2 = null, nvmeTs3 = null, nvmeTs4 = null;
                            // NVMe Identify/Namespace 附加信息
                            string? nvmeEui64 = null, nvmeNguid = null;
                            int? nvmeNsLbaSizeBytes = null;
                            ulong? nvmeNszeLba = null, nvmeNuseLba = null;
                            ulong? nvmeNsCapacityBytes = null, nvmeNsInUseBytes = null;
                            uint? nvmeNamespaceCount = null;
                            string? overall = null;
                            // NVMe 错误日志（Log 0x01）对象（按盘）
                            object? nvmeErrorLogObj = null;

                            // Phase B：原生 IOCTL 优先（可通过环境变量禁用）
                            try
                            {
                                var idx = TryExtractIndex(id);
                                if (!disableNative && idx.HasValue)
                                {
                                    StorageIoctlHelper.SmartSummary? native = null;
                                    if (!string.IsNullOrWhiteSpace(iface) && iface.IndexOf("nvme", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                        native = StorageIoctlHelper.TryReadNvmeSmartSummary(idx.Value);
                                    else
                                        native = StorageIoctlHelper.TryReadAtaSmartSummary(idx.Value);
                                    if (native != null)
                                    {
                                        overall = overall ?? native.overall_health;
                                        temperature ??= native.temperature_c;
                                        pwrOnHours ??= native.power_on_hours;
                                        realloc ??= native.reallocated_sector_count;
                                        pending ??= native.pending_sector_count;
                                        udmaCrc ??= native.udma_crc_error_count;
                                        nvmePctUsed ??= native.nvme_percentage_used;
                                        dataRead ??= native.nvme_data_units_read;
                                        dataWritten ??= native.nvme_data_units_written;
                                        ctrlBusyMin ??= native.nvme_controller_busy_time_min;
                                        unsafeShutdowns ??= native.unsafe_shutdowns;
                                        throttleEvents ??= native.thermal_throttle_events;
                                        // 新增 NVMe 字段
                                        nvmePowerCycles ??= native.nvme_power_cycles;
                                        nvmePoh ??= native.nvme_power_on_hours;
                                        nvmeMediaErrors ??= native.nvme_media_errors;
                                        nvmeAvailSpare ??= native.nvme_available_spare;
                                        nvmeSpareThreshold ??= native.nvme_spare_threshold;
                                        nvmeCriticalWarning ??= native.nvme_critical_warning;
                                        nvmeTs1 ??= native.nvme_temp_sensor1_c;
                                        nvmeTs2 ??= native.nvme_temp_sensor2_c;
                                        nvmeTs3 ??= native.nvme_temp_sensor3_c;
                                        nvmeTs4 ??= native.nvme_temp_sensor4_c;
                                        if (smartDebug)
                                        {
                                            try { Serilog.Log.Information("[SMART] native ok disk id={Id} iface={Iface} idx={Idx} temp={T} used%={U} readGB={R} writeGB={W}", id, iface, idx, temperature, nvmePctUsed, dataRead, dataWritten); } catch { }
                                        }
                                    }
                                    else if (smartDebug)
                                    {
                                        try { Serilog.Log.Information("[SMART] native fail disk id={Id} iface={Iface} idx={Idx}", id, iface, idx); } catch { }
                                    }

                                    // 尝试读取 NVMe 错误日志（仅 NVMe 或接口未知时）
                                    object? nvmeErrLogObjLocal = null;
                                    try
                                    {
                                        if (((!string.IsNullOrWhiteSpace(iface)) && iface.IndexOf("nvme", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                            || string.IsNullOrWhiteSpace(iface))
                                        {
                                            var err = TryReadNvmeErrorLogSummaryCached(idx.Value);
                                            if (err != null)
                                            {
                                                nvmeErrLogObjLocal = new
                                                {
                                                    total_nonzero_entries = err.total_nonzero_entries,
                                                    recent_entries = err.recent_entries
                                                        .Select(e => new { e.error_count, e.sqid, e.cmdid, e.status, e.nsid, e.lba })
                                                        .ToArray()
                                                };
                                            }
                                        }
                                    }
                                    catch { }
                                    nvmeErrorLogObj = nvmeErrLogObjLocal ?? nvmeErrorLogObj;

                                    // 放宽 NVMe 判定：若原生失败且 interface_type 为空，则尝试 NVMe SMART 作为备选
                                    if (native == null && string.IsNullOrWhiteSpace(iface))
                                    {
                                        var nvmeAlt = StorageIoctlHelper.TryReadNvmeSmartSummary(idx.Value);
                                        if (nvmeAlt != null)
                                        {
                                            overall = overall ?? nvmeAlt.overall_health;
                                            temperature ??= nvmeAlt.temperature_c;
                                            pwrOnHours ??= nvmeAlt.power_on_hours;
                                            // SATA 专有计数不从 NVMe 覆盖
                                            nvmePctUsed ??= nvmeAlt.nvme_percentage_used;
                                            dataRead ??= nvmeAlt.nvme_data_units_read;
                                            dataWritten ??= nvmeAlt.nvme_data_units_written;
                                            ctrlBusyMin ??= nvmeAlt.nvme_controller_busy_time_min;
                                            unsafeShutdowns ??= nvmeAlt.unsafe_shutdowns;
                                            throttleEvents ??= nvmeAlt.thermal_throttle_events;
                                            nvmePowerCycles ??= nvmeAlt.nvme_power_cycles;
                                            nvmePoh ??= nvmeAlt.nvme_power_on_hours;
                                            nvmeMediaErrors ??= nvmeAlt.nvme_media_errors;
                                            nvmeAvailSpare ??= nvmeAlt.nvme_available_spare;
                                            nvmeSpareThreshold ??= nvmeAlt.nvme_spare_threshold;
                                            nvmeCriticalWarning ??= nvmeAlt.nvme_critical_warning;
                                            nvmeTs1 ??= nvmeAlt.nvme_temp_sensor1_c;
                                            nvmeTs2 ??= nvmeAlt.nvme_temp_sensor2_c;
                                            nvmeTs3 ??= nvmeAlt.nvme_temp_sensor3_c;
                                            nvmeTs4 ??= nvmeAlt.nvme_temp_sensor4_c;
                                            if (smartDebug)
                                            {
                                                try { Serilog.Log.Information("[SMART] nvme fallback ok (iface empty) disk id={Id} idx={Idx}", id, idx); } catch { }
                                            }
                                        }
                                        else if (smartDebug)
                                        {
                                            try { Serilog.Log.Information("[SMART] nvme fallback fail (iface empty) disk id={Id} idx={Idx}", id, idx); } catch { }
                                        }
                                    }
                                }
                            }
                            catch { }

                            // NVMe Identify/Namespace 基本信息（不影响健康读取失败与否）
                            try
                            {
                                var idx = TryExtractIndex(id);
                                if (idx.HasValue && (((!string.IsNullOrWhiteSpace(iface)) && iface.IndexOf("nvme", System.StringComparison.OrdinalIgnoreCase) >= 0) || string.IsNullOrWhiteSpace(iface)))
                                {
                                    // 读取并缓存 Identify Controller/Namespace 基本信息
                                    var nsInfo = TryReadNvmeNamespaceInfoCached(idx.Value);
                                    if (nsInfo.HasValue) { nvmeNamespaceCount = nsInfo.Value.nn; }
                                    uint bestNsid = nsInfo?.bestNsid ?? Collectors.StorageIoctlHelper.TrySelectBestNvmeNamespaceId(idx.Value);
                                    var nsBasic = nsInfo.HasValue ? (int?)nsInfo.Value.lbaBytes is null ? null : (Collectors.StorageIoctlHelper.TryReadNvmeIdentifyNamespaceBasic(idx.Value, bestNsid)) : Collectors.StorageIoctlHelper.TryReadNvmeIdentifyNamespaceBasic(idx.Value, bestNsid);
                                    if (nsBasic.HasValue)
                                    {
                                        var (nsze, nuse, lbaBytes, eui, nguid) = nsBasic.Value;
                                        nvmeNszeLba = nsze;
                                        nvmeNuseLba = nuse;
                                        nvmeNsLbaSizeBytes = lbaBytes;
                                        nvmeEui64 = eui;
                                        nvmeNguid = nguid;
                                        nvmeNsCapacityBytes = (ulong)lbaBytes * nsze;
                                        nvmeNsInUseBytes = (ulong)lbaBytes * nuse;
                                    }
                                    if (smartDebug)
                                    {
                                        try { Serilog.Log.Information("[SMART] NVMe NS info idx={Idx} NN={NN} bestNSID={NSID} LBA={LBA} nsze={NSZE} nuse={NUSE} eui64={EUI} nguid={NGUID}", idx, nvmeNamespaceCount, bestNsid, nvmeNsLbaSizeBytes, nvmeNszeLba, nvmeNuseLba, nvmeEui64, nvmeNguid); } catch { }
                                    }
                                }
                            }
                            catch { }

                            if (sensors != null)
                            {
                                // 温度
                                temperature ??= FindVal(sensors, "Temperature");

                                // SATA 常见 SMART
                                pwrOnHours ??= FindVal(sensors, "Power On Hours", "Power-On Hours", "POH");
                                realloc ??= FindVal(sensors, "Reallocated Sectors", "Reallocated Sector", "Realloc");
                                pending ??= FindVal(sensors, "Current Pending Sector", "Pending Sectors", "C5", "Pending");
                                udmaCrc ??= FindVal(sensors, "CRC Error", "UDMA CRC Error", "Interface CRC Error", "CRC Error Count");

                                // NVMe 常见指标（不同固件/控制器命名略有差异，尽量覆盖）
                                // 剩余寿命/已使用百分比：若存在 Remaining Life(%)，则 used = 100 - remaining
                                var remainingLife = FindVal(sensors, "Remaining Life", "Life Remaining", "Life Left", "Remaining Life(%)");
                                var wearLevel = FindVal(sensors, "Wear", "Percentage Used", "Wear Level", "Media Wearout Indicator", "Wearout");
                                if (!nvmePctUsed.HasValue)
                                {
                                    if (wearLevel.HasValue) nvmePctUsed = wearLevel; // 已直接是使用百分比
                                    else if (remainingLife.HasValue) nvmePctUsed = 100 - remainingLife;
                                }

                                // 总读写量（一般单位是 GB 或者 GiB，由 LHM 规范化为数值，直接透传数值）
                                dataRead ??= FindVal(sensors, "Total Host Reads", "Data Read", "Bytes Read", "Host Reads", "Data Units Read", "Read Total");
                                dataWritten ??= FindVal(sensors, "Total Host Writes", "Data Written", "Bytes Written", "Host Writes", "Data Units Written", "Write Total");

                                // 控制器忙碌时间/非正常关机/热节流事件
                                ctrlBusyMin ??= FindVal(sensors, "Controller Busy Time", "Busy Time");
                                unsafeShutdowns ??= FindVal(sensors, "Unsafe Shutdown", "Unsafe Shutdowns");
                                throttleEvents ??= FindVal(sensors, "Thermal Throttling", "Thermal Throttle", "Throttle Events");

                                // 健康概览（若存在 Health 百分比等）
                                var healthPct = FindVal(sensors, "Health");
                                if (!string.IsNullOrEmpty(overall) == false && healthPct.HasValue)
                                {
                                    overall = healthPct.Value >= 90 ? "good" : (healthPct.Value >= 50 ? "warning" : "critical");
                                }
                            }

                            outList.Add(new
                            {
                                disk_id = id,
                                overall_health = overall,
                                temperature_c = temperature,
                                power_on_hours = pwrOnHours,
                                reallocated_sector_count = realloc,
                                pending_sector_count = pending,
                                udma_crc_error_count = udmaCrc,
                                nvme_percentage_used = nvmePctUsed,
                                nvme_data_units_read = dataRead,
                                nvme_data_units_written = dataWritten,
                                nvme_controller_busy_time_min = ctrlBusyMin,
                                unsafe_shutdowns = unsafeShutdowns,
                                // 新增 NVMe 字段输出（有值才返回数值，否则为 null）
                                nvme_power_cycles = nvmePowerCycles,
                                nvme_power_on_hours = nvmePoh,
                                nvme_media_errors = nvmeMediaErrors,
                                nvme_available_spare = nvmeAvailSpare,
                                nvme_spare_threshold = nvmeSpareThreshold,
                                nvme_critical_warning = nvmeCriticalWarning,
                                nvme_temp_sensor1_c = nvmeTs1,
                                nvme_temp_sensor2_c = nvmeTs2,
                                nvme_temp_sensor3_c = nvmeTs3,
                                nvme_temp_sensor4_c = nvmeTs4,
                                thermal_throttle_events = throttleEvents,
                                // 新增：NVMe 错误日志汇总输出
                                nvme_error_log = nvmeErrorLogObj,
                                // NVMe Identify/Namespace 附加输出
                                nvme_namespace_count = nvmeNamespaceCount,
                                nvme_namespace_lba_size_bytes = nvmeNsLbaSizeBytes,
                                nvme_namespace_size_lba = nvmeNszeLba,
                                nvme_namespace_inuse_lba = nvmeNuseLba,
                                nvme_namespace_capacity_bytes = nvmeNsCapacityBytes,
                                nvme_namespace_inuse_bytes = nvmeNsInUseBytes,
                                nvme_eui64 = nvmeEui64,
                                nvme_nguid = nvmeNguid
                            });
                        }
                        if (outList.Count > 0) smartHealth = outList.ToArray();
                    }
                }
                catch { }
                // 更新 SMART 缓存（若本轮成功构建）
                try
                {
                    if (!smartFromCache && smartHealth != null)
                    {
                        lock (_slowLock) { _smartCache = smartHealth; _smartAtMs = nowMs; }
                    }
                }
                catch { }

                // Top processes by disk I/O (read/write bytes per second)
                object[] topProcDisk = System.Array.Empty<object>();
                try
                {
                    int topN = 10;
                    var env = System.Environment.GetEnvironmentVariable("SYS_SENSOR_TOPPROC_DISK_N");
                    if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var n) && n > 0 && n <= 50) topN = n;
                    topProcDisk = SystemMonitor.Service.Services.ProcessDiskIoSampler.Instance.ReadTopByBytes(topN);
                }
                catch { }

                return new
                {
                    // 顶层保持兼容
                    // 若 basic 为 0，也采用 totals 的回退结果
                    read_bytes_per_sec = finalRead,
                    write_bytes_per_sec = finalWrite,
                    queue_length = (double)basic.GetType().GetProperty("queue_length")!.GetValue(basic)!,
                    totals_source = totalsSource,
                    // 扩展输出
                    totals,
                    per_physical_disk_io = perInst,
                    per_volume_io = (object?)(perVolEnriched ?? perVol),
                    top_processes_by_disk = topProcDisk,
                    // 容量与静态信息
                    capacity_totals = new { total_bytes = capTotals.total, used_bytes = capTotals.used, free_bytes = capTotals.free },
                    vm_swapfiles_bytes = vmSwapBytes,
                    // 平台占位（APFS 相关：Windows 下恒为 null）
                    purgeable_space_bytes = (long?)null,
                    apfs_local_snapshots_count = (int?)null,
                    apfs_local_snapshots_bytes = (long?)null,
                    per_volume = vols,
                    per_physical_disk = physOut,
                    smart_health = (object[]?)smartHealth
                };
            }
            catch
            {
                return new
                {
                    read_bytes_per_sec = 0L,
                    write_bytes_per_sec = 0L,
                    queue_length = 0.0,
                    totals = new
                    {
                        read_bytes_per_sec = 0L,
                        write_bytes_per_sec = 0L,
                        read_iops = (double?)null,
                        write_iops = (double?)null,
                        busy_percent = (double?)null,
                        queue_length = (double?)null,
                        avg_read_latency_ms = (double?)null,
                        avg_write_latency_ms = (double?)null
                    },
                    per_physical_disk_io = System.Array.Empty<object>(),
                    per_volume_io = System.Array.Empty<object>(),
                    top_processes_by_disk = System.Array.Empty<object>(),
                    capacity_totals = new { total_bytes = (long?)null, used_bytes = (long?)null, free_bytes = (long?)null },
                    // 平台占位（APFS 相关：Windows 下恒为 null）
                    purgeable_space_bytes = (long?)null,
                    apfs_local_snapshots_count = (int?)null,
                    apfs_local_snapshots_bytes = (long?)null,
                    per_volume = System.Array.Empty<object>(),
                    per_physical_disk = System.Array.Empty<object>()
                };
            }
        }
        // ===== NVMe 缓存辅助方法 =====
        private static (string sn, string mn, string fr)? TryReadNvmeIdentifyStringsCached(int idx)
        {
            var now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_nvmeLock)
            {
                if (_nvmeIdentifyStrCache.TryGetValue(idx, out var e) && (now - e.atMs) < NVME_IDENT_TTL_MS)
                    return e.data;
            }
            try
            {
                var v = StorageIoctlHelper.TryReadNvmeIdentifyStrings(idx);
                lock (_nvmeLock) { _nvmeIdentifyStrCache[idx] = (now, v); }
                return v;
            }
            catch { lock (_nvmeLock) { if (_nvmeIdentifyStrCache.TryGetValue(idx, out var e)) return e.data; } return null; }
        }

        private static (uint bestNsid, int lbaBytes, ulong nsze, ulong nuse, string? eui64, string? nguid, uint? nn)? TryReadNvmeNamespaceInfoCached(int idx)
        {
            var now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_nvmeLock)
            {
                if (_nvmeNsInfoCache.TryGetValue(idx, out var e) && (now - e.atMs) < NVME_IDENT_TTL_MS)
                    return e.Item2;
            }
            try
            {
                uint? nn = Collectors.StorageIoctlHelper.TryReadNvmeIdentifyControllerNN(idx);
                uint best = Collectors.StorageIoctlHelper.TrySelectBestNvmeNamespaceId(idx);
                var nsBasic = Collectors.StorageIoctlHelper.TryReadNvmeIdentifyNamespaceBasic(idx, best);
                (uint, int, ulong, ulong, string?, string?, uint?)? pack = null;
                if (nsBasic.HasValue)
                {
                    var (nsze, nuse, lbaBytes, eui, nguid) = nsBasic.Value;
                    pack = (best, lbaBytes, nsze, nuse, eui, nguid, nn);
                }
                lock (_nvmeLock) { _nvmeNsInfoCache[idx] = (now, pack); }
                return pack;
            }
            catch { lock (_nvmeLock) { if (_nvmeNsInfoCache.TryGetValue(idx, out var e)) return e.Item2; } return null; }
        }

        private static StorageIoctlHelper.NvmeErrorLogSummary? TryReadNvmeErrorLogSummaryCached(int idx)
        {
            var now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_nvmeLock)
            {
                if (_nvmeErrorLogCache.TryGetValue(idx, out var e) && (now - e.atMs) < NVME_ERRLOG_TTL_MS)
                    return e.obj as StorageIoctlHelper.NvmeErrorLogSummary;
            }
            try
            {
                var err = StorageIoctlHelper.TryReadNvmeErrorLogSummary(idx);
                lock (_nvmeLock) { _nvmeErrorLogCache[idx] = (now, err); }
                return err;
            }
            catch { lock (_nvmeLock) { if (_nvmeErrorLogCache.TryGetValue(idx, out var e)) return e.obj as StorageIoctlHelper.NvmeErrorLogSummary; } return null; }
        }
    }
}
