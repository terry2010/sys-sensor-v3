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

                // smart/温度：使用 LibreHardwareMonitor 作为回退，按 model 名称模糊匹配
                object[]? smartHealth = null;
                try
                {
                    var physList = physOut as IEnumerable<object>;
                    if (physList != null)
                    {
                        var lhm = SensorsProvider.Current.DumpAll();
                        var storageTemps = new List<(string hwName, double val)>();
                        foreach (var s in lhm)
                        {
                            if (string.Equals(s.hw_type, "Storage", System.StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(s.sensor_type, "Temperature", System.StringComparison.OrdinalIgnoreCase) && s.value.HasValue)
                            {
                                storageTemps.Add((s.hw_name ?? string.Empty, s.value.Value));
                            }
                        }
                        var outList = new List<object>();
                        foreach (var p in physList)
                        {
                            var id = GetStringN(p, "id") ?? string.Empty;
                            var model = GetStringN(p, "model");
                            double? temp = null;
                            if (!string.IsNullOrWhiteSpace(model))
                            {
                                var cand = storageTemps.FirstOrDefault(t => t.hwName?.IndexOf(model, System.StringComparison.OrdinalIgnoreCase) >= 0);
                                if (!string.IsNullOrEmpty(cand.hwName)) temp = cand.val;
                            }
                            outList.Add(new
                            {
                                disk_id = id,
                                overall_health = (string?)null,
                                temperature_c = temp,
                                power_on_hours = (double?)null,
                                reallocated_sector_count = (double?)null,
                                pending_sector_count = (double?)null,
                                udma_crc_error_count = (double?)null,
                                nvme_percentage_used = (double?)null,
                                nvme_data_units_read = (double?)null,
                                nvme_data_units_written = (double?)null,
                                nvme_controller_busy_time_min = (double?)null,
                                unsafe_shutdowns = (double?)null,
                                thermal_throttle_events = (double?)null
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
