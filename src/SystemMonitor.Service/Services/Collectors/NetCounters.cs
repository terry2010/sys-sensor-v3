using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class NetCounters
    {
        private static readonly Lazy<NetCounters> _inst = new(() => new NetCounters());
        public static NetCounters Instance => _inst.Value;

        private sealed class IfCounters
        {
            public string Name { get; }
            // Bytes/sec
            public System.Diagnostics.PerformanceCounter? SentBytes { get; }
            public System.Diagnostics.PerformanceCounter? RecvBytes { get; }
            // Packets/sec
            public System.Diagnostics.PerformanceCounter? SentPackets { get; }
            public System.Diagnostics.PerformanceCounter? RecvPackets { get; }
            // Errors/sec（注意部分计数器为“每秒值”，不同系统版本可能不可用）
            public System.Diagnostics.PerformanceCounter? OutboundErrors { get; }
            public System.Diagnostics.PerformanceCounter? ReceivedErrors { get; }
            // Drops/Discarded per sec（若不可用则为 null）
            public System.Diagnostics.PerformanceCounter? OutboundDiscarded { get; }
            public System.Diagnostics.PerformanceCounter? ReceivedDiscarded { get; }

            public IfCounters(
                string name,
                System.Diagnostics.PerformanceCounter? sentBytes,
                System.Diagnostics.PerformanceCounter? recvBytes,
                System.Diagnostics.PerformanceCounter? sentPackets,
                System.Diagnostics.PerformanceCounter? recvPackets,
                System.Diagnostics.PerformanceCounter? outboundErrors,
                System.Diagnostics.PerformanceCounter? receivedErrors,
                System.Diagnostics.PerformanceCounter? outboundDiscarded,
                System.Diagnostics.PerformanceCounter? receivedDiscarded)
            {
                Name = name;
                SentBytes = sentBytes; RecvBytes = recvBytes;
                SentPackets = sentPackets; RecvPackets = recvPackets;
                OutboundErrors = outboundErrors; ReceivedErrors = receivedErrors;
                OutboundDiscarded = outboundDiscarded; ReceivedDiscarded = receivedDiscarded;
            }
        }

        private List<IfCounters>? _ifs;
        private long _lastTicks;
        private object? _lastPayload;
        private bool _initTried;

        // Fallback: 使用 NetworkInterface 的累计统计做差分
        private long _niLastAt;
        private readonly Dictionary<string, (long rxB, long txB, long rxPk, long txPk, long rxErr, long txErr, long rxDrop, long txDrop)> _niLast = new(StringComparer.OrdinalIgnoreCase);

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
                        // Bytes/sec
                        var sentBytes = new System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Sent/sec", inst, readOnly: true);
                        var recvBytes = new System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Received/sec", inst, readOnly: true);
                        // Packets/sec
                        System.Diagnostics.PerformanceCounter? sentPk = null, recvPk = null;
                        try { sentPk = new System.Diagnostics.PerformanceCounter("Network Interface", "Packets Sent/sec", inst, readOnly: true); } catch { }
                        try { recvPk = new System.Diagnostics.PerformanceCounter("Network Interface", "Packets Received/sec", inst, readOnly: true); } catch { }
                        // Errors/sec
                        System.Diagnostics.PerformanceCounter? outErr = null, inErr = null;
                        try { inErr = new System.Diagnostics.PerformanceCounter("Network Interface", "Packets Received Errors", inst, readOnly: true); } catch { }
                        try { outErr = new System.Diagnostics.PerformanceCounter("Network Interface", "Packets Outbound Errors", inst, readOnly: true); } catch { }
                        // Drops/Discarded per sec
                        System.Diagnostics.PerformanceCounter? outDrop = null, inDrop = null;
                        try { inDrop = new System.Diagnostics.PerformanceCounter("Network Interface", "Packets Received Discarded", inst, readOnly: true); } catch { }
                        try { outDrop = new System.Diagnostics.PerformanceCounter("Network Interface", "Packets Outbound Discarded", inst, readOnly: true); } catch { }

                        // 预热一次读数（避免首次返回0导致抖动）
                        try { _ = sentBytes.NextValue(); } catch { }
                        try { _ = recvBytes.NextValue(); } catch { }
                        try { if (sentPk != null) _ = sentPk.NextValue(); } catch { }
                        try { if (recvPk != null) _ = recvPk.NextValue(); } catch { }
                        try { if (outErr != null) _ = outErr.NextValue(); } catch { }
                        try { if (inErr != null) _ = inErr.NextValue(); } catch { }
                        try { if (outDrop != null) _ = outDrop.NextValue(); } catch { }
                        try { if (inDrop != null) _ = inDrop.NextValue(); } catch { }

                        list.Add(new IfCounters(inst, sentBytes, recvBytes, sentPk, recvPk, outErr, inErr, outDrop, inDrop));
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
            long sumPkRx = 0, sumPkTx = 0; int cntPkRx = 0, cntPkTx = 0;
            long sumErrRx = 0, sumErrTx = 0; int cntErrRx = 0, cntErrTx = 0;
            long sumDropRx = 0, sumDropTx = 0; int cntDropRx = 0, cntDropTx = 0;
            var perIf = new List<object>();
            if (_ifs != null && _ifs.Count > 0)
            {
                foreach (var item in _ifs)
                {
                    long rx = 0, tx = 0;
                    long? pkRx = null, pkTx = null;
                    long? errRx = null, errTx = null;
                    long? dropRx = null, dropTx = null;
                    try { if (item.RecvBytes != null) rx = (long)item.RecvBytes.NextValue(); } catch { }
                    try { if (item.SentBytes != null) tx = (long)item.SentBytes.NextValue(); } catch { }
                    try { if (item.RecvPackets != null) pkRx = (long)Math.Round(item.RecvPackets.NextValue()); } catch { }
                    try { if (item.SentPackets != null) pkTx = (long)Math.Round(item.SentPackets.NextValue()); } catch { }
                    try { if (item.ReceivedErrors != null) errRx = (long)Math.Round(item.ReceivedErrors.NextValue()); } catch { }
                    try { if (item.OutboundErrors != null) errTx = (long)Math.Round(item.OutboundErrors.NextValue()); } catch { }
                    try { if (item.ReceivedDiscarded != null) dropRx = (long)Math.Round(item.ReceivedDiscarded.NextValue()); } catch { }
                    try { if (item.OutboundDiscarded != null) dropTx = (long)Math.Round(item.OutboundDiscarded.NextValue()); } catch { }

                    totalRx += rx; totalTx += tx;
                    if (pkRx.HasValue) { sumPkRx += pkRx.Value; cntPkRx++; }
                    if (pkTx.HasValue) { sumPkTx += pkTx.Value; cntPkTx++; }
                    if (errRx.HasValue) { sumErrRx += errRx.Value; cntErrRx++; }
                    if (errTx.HasValue) { sumErrTx += errTx.Value; cntErrTx++; }
                    if (dropRx.HasValue) { sumDropRx += dropRx.Value; cntDropRx++; }
                    if (dropTx.HasValue) { sumDropTx += dropTx.Value; cntDropTx++; }
                    perIf.Add(new
                    {
                        if_id = item.Name, // 暂以实例名占位，待 NetworkQuery 提供 IfIndex/Guid 再替换
                        name = item.Name,
                        rx_bytes_per_sec = rx,
                        tx_bytes_per_sec = tx,
                        rx_packets_per_sec = pkRx,
                        tx_packets_per_sec = pkTx,
                        rx_errors_per_sec = errRx,
                        tx_errors_per_sec = errTx,
                        rx_drops_per_sec = dropRx,
                        tx_drops_per_sec = dropTx,
                        utilization_percent = (double?)null,
                    });
                }
            }
            else
            {
                // 回退路径：使用 IPv4 统计的累计值计算每秒速率
                double secs = (_niLastAt == 0) ? 1.0 : Math.Max(0.2, (now - _niLastAt) / 1000.0); // 与节流一致，至少 200ms
                try
                {
                    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (!IsValidInterface(ni.Name)) continue;
                        try
                        {
                            var st = ni.GetIPv4Statistics();
                            long rxB = st.BytesReceived;
                            long txB = st.BytesSent;
                            long rxPk = st.UnicastPacketsReceived + st.NonUnicastPacketsReceived;
                            long txPk = st.UnicastPacketsSent + st.NonUnicastPacketsSent;
                            long rxErr = st.IncomingPacketsWithErrors;
                            long txErr = st.OutgoingPacketsWithErrors;
                            long rxDrop = st.IncomingPacketsDiscarded;
                            long txDrop = st.OutgoingPacketsDiscarded;

                            var key = ni.Id ?? ni.Name;
                            _niLast.TryGetValue(key, out var last);
                            long dRxB = Math.Max(0, rxB - last.rxB);
                            long dTxB = Math.Max(0, txB - last.txB);
                            long dRxPk = Math.Max(0, rxPk - last.rxPk);
                            long dTxPk = Math.Max(0, txPk - last.txPk);
                            long dRxErr = Math.Max(0, rxErr - last.rxErr);
                            long dTxErr = Math.Max(0, txErr - last.txErr);
                            long dRxDrop = Math.Max(0, rxDrop - last.rxDrop);
                            long dTxDrop = Math.Max(0, txDrop - last.txDrop);

                            long rx = (long)Math.Round(dRxB / secs);
                            long tx = (long)Math.Round(dTxB / secs);
                            long? pkRx = (long)Math.Round(dRxPk / secs);
                            long? pkTx = (long)Math.Round(dTxPk / secs);
                            long? errRx = (long)Math.Round(dRxErr / secs);
                            long? errTx = (long)Math.Round(dTxErr / secs);
                            long? dropRx = (long)Math.Round(dRxDrop / secs);
                            long? dropTx = (long)Math.Round(dTxDrop / secs);

                            totalRx += rx; totalTx += tx;
                            if (pkRx.HasValue) { sumPkRx += pkRx.Value; cntPkRx++; }
                            if (pkTx.HasValue) { sumPkTx += pkTx.Value; cntPkTx++; }
                            if (errRx.HasValue) { sumErrRx += errRx.Value; cntErrRx++; }
                            if (errTx.HasValue) { sumErrTx += errTx.Value; cntErrTx++; }
                            if (dropRx.HasValue) { sumDropRx += dropRx.Value; cntDropRx++; }
                            if (dropTx.HasValue) { sumDropTx += dropTx.Value; cntDropTx++; }

                            perIf.Add(new
                            {
                                if_id = key,
                                name = ni.Name,
                                rx_bytes_per_sec = rx,
                                tx_bytes_per_sec = tx,
                                rx_packets_per_sec = pkRx,
                                tx_packets_per_sec = pkTx,
                                rx_errors_per_sec = errRx,
                                tx_errors_per_sec = errTx,
                                rx_drops_per_sec = dropRx,
                                tx_drops_per_sec = dropTx,
                                utilization_percent = (double?)null,
                            });

                            // 更新 last
                            _niLast[key] = (rxB, txB, rxPk, txPk, rxErr, txErr, rxDrop, txDrop);
                        }
                        catch { }
                    }
                }
                catch { }
                _niLastAt = now;
            }

            var payload = new
            {
                io_totals = new
                {
                    rx_bytes_per_sec = totalRx,
                    tx_bytes_per_sec = totalTx,
                    rx_packets_per_sec = cntPkRx > 0 ? sumPkRx : (long?)null,
                    tx_packets_per_sec = cntPkTx > 0 ? sumPkTx : (long?)null,
                    rx_errors_per_sec = cntErrRx > 0 ? sumErrRx : (long?)null,
                    tx_errors_per_sec = cntErrTx > 0 ? sumErrTx : (long?)null,
                    rx_drops_per_sec = cntDropRx > 0 ? sumDropRx : (long?)null,
                    tx_drops_per_sec = cntDropTx > 0 ? sumDropTx : (long?)null,
                },
                per_interface_io = perIf.ToArray()
            };

            _lastPayload = payload;
            _lastTicks = now;
            return payload;
        }
    }
}
