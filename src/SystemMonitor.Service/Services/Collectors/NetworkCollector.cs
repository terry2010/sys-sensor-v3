using System;
using System.Collections.Generic;
using SystemMonitor.Service.Services.Queries;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class NetworkCollector : IMetricsCollector
    {
        public string Name => "network";

        private static object? GetProp(object o, string name)
        {
            if (o == null) return null;
            try { return o.GetType().GetProperty(name)?.GetValue(o); } catch { return null; }
        }

        public object? Collect()
        {
            object? nc = null; object? nq = null;
            try { nc = NetCounters.Instance.Read(); } catch { nc = null; }
            try { nq = NetworkQuery.Instance.Read(); } catch { nq = null; }

            // 提取 counters
            var ioTotals = nc != null ? GetProp(nc, "io_totals") : null;
            var perIoObj = nc != null ? GetProp(nc, "per_interface_io") as System.Collections.IEnumerable : null;

            // 提取查询信息
            var infoObj = nq != null ? GetProp(nq, "per_interface_info") as System.Collections.IEnumerable : null;
            var ethObj = nq != null ? GetProp(nq, "per_ethernet_info") as System.Collections.IEnumerable : null;

            // 构建 name -> link_speed_mbps 的映射（取最大可用）
            var speedMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            void TryPut(object? row)
            {
                if (row == null) return;
                try
                {
                    var name = GetProp(row, "name") as string;
                    var raw = GetProp(row, "link_speed_mbps");
                    long? sp = null;
                    if (raw != null)
                    {
                        try { sp = Convert.ToInt64(raw); } catch { }
                    }
                    if (!string.IsNullOrWhiteSpace(name) && sp.HasValue && sp.Value > 0)
                    {
                        if (!speedMap.TryGetValue(name!, out var old) || sp.Value > old)
                            speedMap[name!] = sp.Value;
                    }
                }
                catch { }
            }
            try { if (infoObj != null) foreach (var it in infoObj) TryPut(it); } catch { }
            try { if (ethObj != null) foreach (var it in ethObj) TryPut(it); } catch { }

            // 重建 per_interface_io，填充 utilization_percent
            var perIoList = new List<object>();
            if (perIoObj != null)
            {
                foreach (var row in perIoObj)
                {
                    try
                    {
                        var name = GetProp(row, "name") as string ?? "";
                        var rx = 0L; var tx = 0L;
                        try { var r = GetProp(row, "rx_bytes_per_sec"); if (r != null) rx = Convert.ToInt64(r); } catch { }
                        try { var t = GetProp(row, "tx_bytes_per_sec"); if (t != null) tx = Convert.ToInt64(t); } catch { }
                        double? util = null;
                        if (!string.IsNullOrEmpty(name) && speedMap.TryGetValue(name, out var mbps) && mbps > 0)
                        {
                            var bps = (rx + tx) * 8.0;
                            var link = mbps * 1_000_000.0;
                            if (link > 0)
                            {
                                util = Math.Max(0.0, Math.Min(1.0, bps / link));
                            }
                        }

                        // 复制原有字段并覆盖 utilization_percent
                        perIoList.Add(new
                        {
                            if_id = GetProp(row, "if_id"),
                            name = name,
                            rx_bytes_per_sec = rx,
                            tx_bytes_per_sec = tx,
                            rx_packets_per_sec = GetProp(row, "rx_packets_per_sec"),
                            tx_packets_per_sec = GetProp(row, "tx_packets_per_sec"),
                            rx_errors_per_sec = GetProp(row, "rx_errors_per_sec"),
                            tx_errors_per_sec = GetProp(row, "tx_errors_per_sec"),
                            rx_drops_per_sec = GetProp(row, "rx_drops_per_sec"),
                            tx_drops_per_sec = GetProp(row, "tx_drops_per_sec"),
                            utilization_percent = util,
                        });
                    }
                    catch { /* ignore row */ }
                }
            }

            // 构造最终返回，尽量保留可用数据
            return new
            {
                io_totals = ioTotals ?? new
                {
                    rx_bytes_per_sec = 0L,
                    tx_bytes_per_sec = 0L,
                    rx_packets_per_sec = (long?)null,
                    tx_packets_per_sec = (long?)null,
                    rx_errors_per_sec = (long?)null,
                    tx_errors_per_sec = (long?)null,
                    rx_drops_per_sec = (long?)null,
                    tx_drops_per_sec = (long?)null,
                },
                per_interface_io = perIoList.ToArray(),
                per_interface_info = infoObj ?? Array.Empty<object>(),
                per_ethernet_info = ethObj ?? Array.Empty<object>(),
            };
        }
    }
}
