using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class NetCounters
    {
        private static readonly Lazy<NetCounters> _inst = new(() => new NetCounters());
        public static NetCounters Instance => _inst.Value;

        private sealed class IfCounters
        {
            public string Name { get; }
            public System.Diagnostics.PerformanceCounter? Sent { get; }
            public System.Diagnostics.PerformanceCounter? Recv { get; }
            public IfCounters(string name, System.Diagnostics.PerformanceCounter? sent, System.Diagnostics.PerformanceCounter? recv)
            {
                Name = name; Sent = sent; Recv = recv;
            }
        }

        private List<IfCounters>? _ifs;
        private long _lastTicks;
        private object? _lastPayload;
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
                var list = new List<IfCounters>();
                foreach (var inst in valid)
                {
                    try
                    {
                        var sent = new System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Sent/sec", inst, readOnly: true);
                        var recv = new System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Received/sec", inst, readOnly: true);
                        // 预热一次读数
                        try { _ = sent.NextValue(); } catch { }
                        try { _ = recv.NextValue(); } catch { }
                        list.Add(new IfCounters(inst, sent, recv));
                    }
                    catch
                    {
                        // ignore this instance
                    }
                }
                _ifs = list;
                _lastTicks = Environment.TickCount64;
                _lastPayload = new
                {
                    io_totals = new
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
                    per_interface_io = Array.Empty<object>()
                };
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
            if (now - _lastTicks < 200 && _lastPayload != null)
            {
                return _lastPayload;
            }

            long totalRx = 0, totalTx = 0;
            var perIf = new List<object>();
            if (_ifs != null)
            {
                foreach (var item in _ifs)
                {
                    long rx = 0, tx = 0;
                    try { if (item.Recv != null) rx = (long)item.Recv.NextValue(); } catch { }
                    try { if (item.Sent != null) tx = (long)item.Sent.NextValue(); } catch { }
                    totalRx += rx; totalTx += tx;
                    perIf.Add(new
                    {
                        if_id = item.Name, // 暂以实例名占位，待 NetworkQuery 提供 IfIndex/Guid 再替换
                        name = item.Name,
                        rx_bytes_per_sec = rx,
                        tx_bytes_per_sec = tx,
                        rx_packets_per_sec = (long?)null,
                        tx_packets_per_sec = (long?)null,
                        rx_errors_per_sec = (long?)null,
                        tx_errors_per_sec = (long?)null,
                        rx_drops_per_sec = (long?)null,
                        tx_drops_per_sec = (long?)null,
                        utilization_percent = (double?)null,
                    });
                }
            }

            var payload = new
            {
                io_totals = new
                {
                    rx_bytes_per_sec = totalRx,
                    tx_bytes_per_sec = totalTx,
                    rx_packets_per_sec = (long?)null,
                    tx_packets_per_sec = (long?)null,
                    rx_errors_per_sec = (long?)null,
                    tx_errors_per_sec = (long?)null,
                    rx_drops_per_sec = (long?)null,
                    tx_drops_per_sec = (long?)null,
                },
                per_interface_io = perIf.ToArray()
            };

            _lastPayload = payload;
            _lastTicks = now;
            return payload;
        }
    }
}
