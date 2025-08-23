using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Management; // WMI/CIM (may be unavailable on some SKUs)

namespace SystemMonitor.Service.Services.Queries
{
    /// <summary>
    /// NetworkQuery 聚合网络接口静态信息与以太网扩展信息（带缓存）。
    /// - 数据来源：System.Net.NetworkInformation + WMI/CIM (MSFT_NetAdapter)
    /// - 缓存：默认 45s（避免频繁访问 WMI）
    /// - 可用性：WMI/CIM 失败时，扩展字段返回 null，保持容错
    /// </summary>
    internal sealed class NetworkQuery
    {
        private static readonly Lazy<NetworkQuery> _inst = new(() => new NetworkQuery());
        public static NetworkQuery Instance => _inst.Value;

        private object? _cache;
        private long _cacheAt;
        private readonly object _lock = new();
        private const int DefaultTtlMs = 45_000;

        public object Read(bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force)
            {
                lock (_lock)
                {
                    if (_cache != null && now - _cacheAt < DefaultTtlMs)
                        return _cache;
                }
            }

            var payload = new
            {
                per_interface_info = GetInterfaceInfo(),
                per_ethernet_info = GetEthernetInfo(),
            };

            lock (_lock)
            {
                _cache = payload;
                _cacheAt = now;
            }
            return payload;
        }

        private static List<object> GetInterfaceInfo()
        {
            var list = new List<object>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        var props = ni.GetIPProperties();
                        var ipv4p = props.GetIPv4Properties();
                        var ifIndex = ipv4p?.Index;
                        var addrs = props.UnicastAddresses?.Select(a => a.Address?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
                        var gws = props.GatewayAddresses?.Select(a => a?.Address?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
                        var dns = props.DnsAddresses?.Select(a => a?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? Array.Empty<string>();
                        var mtu = ipv4p?.Mtu;
                        var mac = ni.GetPhysicalAddress()?.ToString();
                        // 速度：bits/s -> Mbps（四舍五入）
                        long? linkSpeedMbps = null;
                        try
                        {
                            var sp = ni.Speed; // bits per second
                            if (sp > 0) linkSpeedMbps = (long)Math.Round(sp / 1_000_000.0);
                        }
                        catch { }

                        list.Add(new
                        {
                            if_id = !string.IsNullOrEmpty(ni.Id) ? ni.Id : (ifIndex?.ToString() ?? ni.Name),
                            name = ni.Name,
                            type = ni.NetworkInterfaceType.ToString(),
                            status = ni.OperationalStatus.ToString(),
                            mac_address = string.IsNullOrWhiteSpace(mac) ? null : mac,
                            mtu = mtu,
                            ip_addresses = addrs,
                            gateways = gws,
                            dns_servers = dns,
                            search_domains = Array.Empty<string>(), // 暂无合适 API，后续通过 GPO/WMI 聚合
                            rx_errors = (long?)null,
                            tx_errors = (long?)null,
                            rx_drops = (long?)null,
                            tx_drops = (long?)null,
                            collisions = (long?)null,
                            link_speed_mbps = linkSpeedMbps,
                        });
                    }
                    catch { /* per-interface 失败忽略 */ }
                }
            }
            catch { /* ignore */ }
            return list;
        }

        private static List<object> GetEthernetInfo()
        {
            // 通过 MSFT_NetAdapter (root/StandardCimv2) 尝试读取以太网扩展（速率/双工/自协商）
            var list = new List<object>();
            ManagementObjectCollection? col = null;
            try
            {
                var scope = new ManagementScope(@"\\.\root\StandardCimv2");
                scope.Connect();
                var query = new ObjectQuery("SELECT Name, InterfaceGuid, ifIndex, LinkSpeed, FullDuplex, AutoNegotiation FROM MSFT_NetAdapter");
                using var searcher = new ManagementObjectSearcher(scope, query);
                col = searcher.Get();
            }
            catch { /* WMI/CIM 不可用时忽略 */ }

            if (col != null)
            {
                foreach (ManagementObject mo in col)
                {
                    try
                    {
                        string? guid = mo["InterfaceGuid"]?.ToString();
                        string? name = mo["Name"]?.ToString();
                        int? ifIndex = null;
                        try { ifIndex = mo["ifIndex"] is int i ? i : (int?)null; } catch { }
                        long? linkMbps = null;
                        try
                        {
                            // LinkSpeed 返回 bits/s
                            if (mo["LinkSpeed"] != null)
                            {
                                var v = Convert.ToInt64(mo["LinkSpeed"]);
                                if (v > 0) linkMbps = (long)Math.Round(v / 1_000_000.0);
                            }
                        }
                        catch { }
                        bool? fullDuplex = null;
                        try { fullDuplex = mo["FullDuplex"] as bool?; } catch { }
                        bool? autoNeg = null;
                        try { autoNeg = mo["AutoNegotiation"] as bool?; } catch { }

                        list.Add(new
                        {
                            if_id = !string.IsNullOrEmpty(guid) ? guid : (ifIndex?.ToString() ?? name ?? "unknown"),
                            name = name ?? "unknown",
                            link_speed_mbps = linkMbps,
                            duplex = fullDuplex.HasValue ? (fullDuplex.Value ? "full" : "half") : null,
                            auto_negotiation = autoNeg,
                        });
                    }
                    catch { /* ignore row */ }
                }
            }
            return list;
        }
    }
}
