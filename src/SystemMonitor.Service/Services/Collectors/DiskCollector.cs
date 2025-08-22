using System.Linq;
using System.Collections.Generic;
using SystemMonitor.Service.Services;
namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class DiskCollector : IMetricsCollector
    {
        public string Name => "disk";
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
                var phys = StorageQuery.Instance.ReadPhysicalDisks();

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
                var totals = new
                {
                    read_bytes_per_sec = finalRead,
                    write_bytes_per_sec = finalWrite,
                    read_iops = GetDoubleN(totalsRaw, "read_iops"),
                    write_iops = GetDoubleN(totalsRaw, "write_iops"),
                    busy_percent = GetDoubleN(totalsRaw, "busy_percent"),
                    queue_length = GetDoubleN(totalsRaw, "queue_length"),
                    avg_read_latency_ms = GetDoubleN(totalsRaw, "avg_read_latency_ms"),
                    avg_write_latency_ms = GetDoubleN(totalsRaw, "avg_write_latency_ms")
                };

                // enrich per_volume_io with free_percent from volumes
                System.Collections.Generic.List<object>? perVolEnriched = null;
                if (perVol != null && vols != null)
                {
                    perVolEnriched = new System.Collections.Generic.List<object>();
                    foreach (var v in perVol)
                    {
                        var vid = GetStringN(v, "volume_id");
                        double? freePct = null;
                        try
                        {
                            var match = vols.FirstOrDefault(x => string.Equals(x.id, vid, System.StringComparison.OrdinalIgnoreCase));
                            freePct = match?.free_percent;
                        }
                        catch { }
                        perVolEnriched.Add(new
                        {
                            volume_id = vid ?? string.Empty,
                            read_bytes_per_sec = GetLong(v, "read_bytes_per_sec"),
                            write_bytes_per_sec = GetLong(v, "write_bytes_per_sec"),
                            read_iops = GetDoubleN(v, "read_iops"),
                            write_iops = GetDoubleN(v, "write_iops"),
                            busy_percent = GetDoubleN(v, "busy_percent"),
                            queue_length = GetDoubleN(v, "queue_length"),
                            avg_read_latency_ms = GetDoubleN(v, "avg_read_latency_ms"),
                            avg_write_latency_ms = GetDoubleN(v, "avg_write_latency_ms"),
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
                try
                {
                    var physList = physOut as IEnumerable<object>;
                    if (physList != null)
                    {
                        // 环境变量开关
                        bool disableNative = string.Equals(System.Environment.GetEnvironmentVariable("SYS_SENSOR_DISABLE_NATIVE_SMART"), "1", System.StringComparison.OrdinalIgnoreCase);
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

                            // 若仍未匹配，且判定为 NVMe，尝试通过 Identify 获取 SN/MN 再匹配
                            int? idxForNvme = TryExtractIndex(id);
                            if (sensors == null && !string.IsNullOrWhiteSpace(iface) && iface.IndexOf("nvme", System.StringComparison.OrdinalIgnoreCase) >= 0 && idxForNvme.HasValue)
                            {
                                try
                                {
                                    var idf = StorageIoctlHelper.TryReadNvmeIdentifyStrings(idxForNvme.Value);
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
                            string? overall = null;

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
                                thermal_throttle_events = throttleEvents
                            });
                        }
                        if (outList.Count > 0) smartHealth = outList.ToArray();
                    }
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
                    // 容量与静态信息
                    capacity_totals = new { total_bytes = capTotals.total, used_bytes = capTotals.used, free_bytes = capTotals.free },
                    vm_swapfiles_bytes = vmSwapBytes,
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
                    capacity_totals = new { total_bytes = (long?)null, used_bytes = (long?)null, free_bytes = (long?)null },
                    per_volume = System.Array.Empty<object>(),
                    per_physical_disk = System.Array.Empty<object>()
                };
            }
        }
    }
}
