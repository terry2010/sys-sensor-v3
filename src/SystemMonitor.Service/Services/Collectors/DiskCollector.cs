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
                if (hasVol)
                {
                    finalRead = sumVolRead;
                    finalWrite = sumVolWrite;
                }
                else if (hasInst)
                {
                    finalRead = sumInstRead;
                    finalWrite = sumInstWrite;
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

                return new
                {
                    // 顶层保持兼容
                    // 若 basic 为 0，也采用 totals 的回退结果
                    read_bytes_per_sec = finalRead,
                    write_bytes_per_sec = finalWrite,
                    queue_length = (double)basic.GetType().GetProperty("queue_length")!.GetValue(basic)!,
                    // 扩展输出
                    totals,
                    per_physical_disk_io = perInst,
                    per_volume_io = perVol,
                    // 容量与静态信息
                    capacity_totals = new { total_bytes = capTotals.total, used_bytes = capTotals.used, free_bytes = capTotals.free },
                    per_volume = vols,
                    per_physical_disk = phys
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
