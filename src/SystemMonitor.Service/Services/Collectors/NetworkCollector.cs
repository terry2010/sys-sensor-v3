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

        private static Dictionary<string, object?> CopyProps(object src)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var props = src.GetType().GetProperties();
                foreach (var p in props)
                {
                    try { dict[p.Name] = p.GetValue(src); } catch { }
                }
            }
            catch { }
            return dict;
        }

        public object? Collect()
        {
            // 为避免单个子查询阻塞整体采集，这里将各子查询并行执行并设置上限超时
            // 基线：计数器尽量快（200-300ms），接口/WMI 查询适度放宽（500-700ms），Wi‑Fi/连通性较快（200-400ms）
            object? nc = null; object? nq = null; object? wq = null; object? conn = null;

            object? TryGetResult(System.Threading.Tasks.Task<object?> t, int timeoutMs)
            {
                try
                {
                    if (t.Wait(timeoutMs)) return t.Result;
                }
                catch { /* ignore */ }
                return null;
            }

            var tNc = System.Threading.Tasks.Task.Run<object?>(() => { try { return NetCounters.Instance.Read(); } catch { return null; } });
            var tNq = System.Threading.Tasks.Task.Run<object?>(() => { try { return NetworkQuery.Instance.Read(); } catch { return null; } });
            var tWq = System.Threading.Tasks.Task.Run<object?>(() => { try { return WifiQuery.Instance.Read(); } catch { return null; } });
            var tConn = System.Threading.Tasks.Task.Run<object?>(() => { try { return ConnectivityService.Instance.Read(); } catch { return null; } });

            // 按各自特性读取结果，未完成则置空，避免拖慢整体 Collect()
            nc = TryGetResult(tNc, 300);
            nq = TryGetResult(tNq, 700);
            wq = TryGetResult(tWq, 400);
            conn = TryGetResult(tConn, 300);

            // 提取 counters
            var ioTotals = nc != null ? GetProp(nc, "io_totals") : null;
            var perIoObj = nc != null ? GetProp(nc, "per_interface_io") as System.Collections.IEnumerable : null;

            // 提取查询信息
            var infoObj = nq != null ? GetProp(nq, "per_interface_info") as System.Collections.IEnumerable : null;
            var ethObj = nq != null ? GetProp(nq, "per_ethernet_info") as System.Collections.IEnumerable : null;
            var wifiInfo = wq != null ? GetProp(wq, "wifi_info") : null;
            var connectivity = conn != null ? GetProp(conn, "connectivity") : null;

            // 构建 name -> if_id 的映射（用于对齐 per_interface_io 的 if_id）
            var ifIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (infoObj != null)
                {
                    foreach (var it in infoObj)
                    {
                        try
                        {
                            var name = GetProp(it, "name") as string;
                            var ifId = GetProp(it, "if_id") as string;
                            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(ifId))
                            {
                                ifIdByName[name!] = ifId!;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

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

                        // 若能从 per_interface_info 映射到真实 if_id，则覆盖占位值
                        object? ifIdOriginal = GetProp(row, "if_id");
                        object? ifIdFinal = ifIdOriginal;
                        if (!string.IsNullOrEmpty(name) && ifIdByName.TryGetValue(name, out var mappedId))
                        {
                            ifIdFinal = mappedId;
                        }

                        // 复制原有字段并覆盖 utilization_percent
                        perIoList.Add(new
                        {
                            if_id = ifIdFinal,
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

            // 修正 wifi_info.if_id（若有 name 匹配）
            object? wifiFixed = wifiInfo;
            try
            {
                if (wifiInfo != null)
                {
                    var wname = GetProp(wifiInfo, "name") as string;
                    var curIfId = GetProp(wifiInfo, "if_id");
                    string? mapped = null;
                    if (!string.IsNullOrWhiteSpace(wname) && ifIdByName.TryGetValue(wname!, out var mid)) mapped = mid;

                    if (!string.IsNullOrWhiteSpace(mapped))
                    {
                        // 复制原有 wifi_info 的所有字段，只覆盖 if_id（并保留 name 原值）
                        var wdict = CopyProps(wifiInfo);
                        wdict["if_id"] = mapped;
                        if (!string.IsNullOrWhiteSpace(wname)) wdict["name"] = wname;
                        wifiFixed = wdict;
                    }
                }
            }
            catch { }

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
                wifi_info = wifiFixed,
                connectivity = connectivity,
            };
        }
    }
}

