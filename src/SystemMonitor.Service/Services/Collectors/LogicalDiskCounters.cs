using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class LogicalDiskCounters
    {
        private static readonly Lazy<LogicalDiskCounters> _inst = new(() => new LogicalDiskCounters());
        public static LogicalDiskCounters Instance => _inst.Value;

        private readonly Dictionary<string, (PerformanceCounter readBps, PerformanceCounter writeBps, PerformanceCounter? reads, PerformanceCounter? writes, PerformanceCounter? idlePct, PerformanceCounter? avgSecRead, PerformanceCounter? avgSecWrite, PerformanceCounter? queue)> _perInst = new(StringComparer.OrdinalIgnoreCase);
        private long _lastTicks;
        private List<object>? _lastPerInst;
        private bool _initTried;

        private void EnsureInit()
        {
            if (_initTried) return;
            _initTried = true;
            try
            {
                var cat = new PerformanceCounterCategory("LogicalDisk");
                foreach (var inst in cat.GetInstanceNames())
                {
                    if (string.Equals(inst, "_Total", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var read = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", inst, readOnly: true);
                        var write = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", inst, readOnly: true);
                        PerformanceCounter? reads = null, writes = null, idle = null, avgR = null, avgW = null, q = null;
                        try { reads = new PerformanceCounter("LogicalDisk", "Disk Reads/sec", inst, readOnly: true); } catch { }
                        try { writes = new PerformanceCounter("LogicalDisk", "Disk Writes/sec", inst, readOnly: true); } catch { }
                        try { idle = new PerformanceCounter("LogicalDisk", "% Idle Time", inst, readOnly: true); } catch { }
                        try { avgR = new PerformanceCounter("LogicalDisk", "Avg. Disk sec/Read", inst, readOnly: true); } catch { }
                        try { avgW = new PerformanceCounter("LogicalDisk", "Avg. Disk sec/Write", inst, readOnly: true); } catch { }
                        try { q = new PerformanceCounter("LogicalDisk", "Current Disk Queue Length", inst, readOnly: true); } catch { }
                        SafePrime(read); SafePrime(write); SafePrime(reads); SafePrime(writes); SafePrime(idle); SafePrime(avgR); SafePrime(avgW); SafePrime(q);
                        _perInst[inst] = (read, write, reads, writes, idle, avgR, avgW, q);
                    }
                    catch { /* ignore instance error */ }
                }
                _lastTicks = Environment.TickCount64;
                _lastPerInst = null;
            }
            catch
            {
                // ignore category error
            }
        }

        private static void SafePrime(PerformanceCounter? c) { try { if (c != null) _ = c.NextValue(); } catch { } }
        private static float? SafeNext(PerformanceCounter? c) { try { return c != null ? c.NextValue() : (float?)null; } catch { return null; } }

        public IReadOnlyList<object> ReadPerVolume()
        {
            EnsureInit();
            var now = Environment.TickCount64;
            if (now - _lastTicks < 200 && _lastPerInst != null)
            {
                return _lastPerInst;
            }

            var list = new List<object>();
            foreach (var kv in _perInst.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var inst = kv.Key; // e.g., C:
                var (read, write, reads, writes, idle, avgR, avgW, q) = kv.Value;
                long rBps = 0, wBps = 0; double? rIops = null, wIops = null, busyPct = null, queue = null, rLat = null, wLat = null;
                try { rBps = (long)(SafeNext(read) ?? 0); } catch { }
                try { wBps = (long)(SafeNext(write) ?? 0); } catch { }
                try { rIops = SafeNext(reads); } catch { }
                try { wIops = SafeNext(writes); } catch { }
                try { var idleV = SafeNext(idle); if (idleV.HasValue) busyPct = Math.Clamp(100.0 - idleV.Value, 0.0, 100.0); } catch { }
                try { queue = SafeNext(q); } catch { }
                try { var v = SafeNext(avgR); if (v.HasValue) rLat = v.Value * 1000.0; } catch { }
                try { var v = SafeNext(avgW); if (v.HasValue) wLat = v.Value * 1000.0; } catch { }
                list.Add(new
                {
                    volume_id = inst,
                    read_bytes_per_sec = rBps,
                    write_bytes_per_sec = wBps,
                    read_iops = rIops,
                    write_iops = wIops,
                    busy_percent = busyPct,
                    queue_length = queue,
                    avg_read_latency_ms = rLat,
                    avg_write_latency_ms = wLat,
                    free_percent = (double?)null // 容量来自 WMI，后续阶段B实现
                });
            }
            _lastPerInst = list;
            _lastTicks = now;
            return list;
        }
    }
}
