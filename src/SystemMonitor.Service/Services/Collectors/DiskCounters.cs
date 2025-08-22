using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class DiskCounters
    {
        private static readonly Lazy<DiskCounters> _inst = new(() => new DiskCounters());
        public static DiskCounters Instance => _inst.Value;

        // _Total 计数器（总览）
        private PerformanceCounter? _readTotal;
        private PerformanceCounter? _writeTotal;
        private PerformanceCounter? _queueTotal;
        private PerformanceCounter? _readsTotal;
        private PerformanceCounter? _writesTotal;
        private PerformanceCounter? _idlePctTotal;
        private PerformanceCounter? _avgSecReadTotal;
        private PerformanceCounter? _avgSecWriteTotal;

        // per-instance 计数器
        private readonly Dictionary<string, (PerformanceCounter readBps, PerformanceCounter writeBps, PerformanceCounter? reads, PerformanceCounter? writes, PerformanceCounter? idlePct, PerformanceCounter? avgSecRead, PerformanceCounter? avgSecWrite, PerformanceCounter? queue)> _perInst = new(StringComparer.OrdinalIgnoreCase);

        private long _lastTicks;
        private (long read, long write, double queue) _last; // 兼容旧 Read()
        private (long readBps, long writeBps, double? busyPct, double? queueLen, double? readLatMs, double? writeLatMs, double? readIops, double? writeIops) _lastTotals;
        private List<object>? _lastPerInst;
        private bool _initTried;

        private void EnsureInit()
        {
            if (_initTried) return;
            _initTried = true;
            try
            {
                // totals
                _readTotal = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
                _writeTotal = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
                _queueTotal = new PerformanceCounter("PhysicalDisk", "Current Disk Queue Length", "_Total", readOnly: true);
                try { _readsTotal = new PerformanceCounter("PhysicalDisk", "Disk Reads/sec", "_Total", readOnly: true); } catch { }
                try { _writesTotal = new PerformanceCounter("PhysicalDisk", "Disk Writes/sec", "_Total", readOnly: true); } catch { }
                try { _idlePctTotal = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", readOnly: true); } catch { }
                try { _avgSecReadTotal = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Read", "_Total", readOnly: true); } catch { }
                try { _avgSecWriteTotal = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Write", "_Total", readOnly: true); } catch { }

                SafePrime(_readTotal); SafePrime(_writeTotal); SafePrime(_queueTotal);
                SafePrime(_readsTotal); SafePrime(_writesTotal); SafePrime(_idlePctTotal);
                SafePrime(_avgSecReadTotal); SafePrime(_avgSecWriteTotal);

                // per instance
                try
                {
                    var cat = new PerformanceCounterCategory("PhysicalDisk");
                    foreach (var inst in cat.GetInstanceNames())
                    {
                        if (string.Equals(inst, "_Total", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", inst, readOnly: true);
                            var write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst, readOnly: true);
                            PerformanceCounter? reads = null, writes = null, idle = null, avgR = null, avgW = null, q = null;
                            try { reads = new PerformanceCounter("PhysicalDisk", "Disk Reads/sec", inst, readOnly: true); } catch { }
                            try { writes = new PerformanceCounter("PhysicalDisk", "Disk Writes/sec", inst, readOnly: true); } catch { }
                            try { idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", inst, readOnly: true); } catch { }
                            try { avgR = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Read", inst, readOnly: true); } catch { }
                            try { avgW = new PerformanceCounter("PhysicalDisk", "Avg. Disk sec/Write", inst, readOnly: true); } catch { }
                            try { q = new PerformanceCounter("PhysicalDisk", "Current Disk Queue Length", inst, readOnly: true); } catch { }
                            SafePrime(read); SafePrime(write); SafePrime(reads); SafePrime(writes); SafePrime(idle); SafePrime(avgR); SafePrime(avgW); SafePrime(q);
                            _perInst[inst] = (read, write, reads, writes, idle, avgR, avgW, q);
                        }
                        catch { /* ignore this instance */ }
                    }
                }
                catch { /* ignore category errors */ }

                _lastTicks = Environment.TickCount64;
                _last = (0, 0, 0.0);
                _lastTotals = default;
                _lastPerInst = null;
            }
            catch
            {
                // ignore; 使用回退
            }
        }

        private static void SafePrime(PerformanceCounter? c) { try { if (c != null) _ = c.NextValue(); } catch { } }
        private static float? SafeNext(PerformanceCounter? c) { try { return c != null ? c.NextValue() : (float?)null; } catch { return null; } }

        public object Read()
        {
            EnsureInit();
            var now = Environment.TickCount64;
            if (now - _lastTicks < 200)
            {
                return new { read_bytes_per_sec = _last.read, write_bytes_per_sec = _last.write, queue_length = _last.queue };
            }
            long r = 0, w = 0; double q = 0.0;
            try { if (_readTotal != null) r = (long)_readTotal.NextValue(); } catch { r = 0; }
            try { if (_writeTotal != null) w = (long)_writeTotal.NextValue(); } catch { w = 0; }
            try { if (_queueTotal != null) q = _queueTotal.NextValue(); } catch { q = 0.0; }
            _last = (r, w, q);
            _lastTicks = now;
            return new { read_bytes_per_sec = r, write_bytes_per_sec = w, queue_length = q };
        }

        // 新增：读取 totals 的扩展字段
        public object ReadTotals()
        {
            EnsureInit();
            var now = Environment.TickCount64;
            if (now - _lastTicks < 200 && _lastTotals.readBps != 0 || _lastTotals.writeBps != 0)
            {
                return new
                {
                    read_bytes_per_sec = _lastTotals.readBps,
                    write_bytes_per_sec = _lastTotals.writeBps,
                    read_iops = _lastTotals.readIops,
                    write_iops = _lastTotals.writeIops,
                    busy_percent = _lastTotals.busyPct,
                    queue_length = _lastTotals.queueLen,
                    avg_read_latency_ms = _lastTotals.readLatMs,
                    avg_write_latency_ms = _lastTotals.writeLatMs
                };
            }

            long readBps = 0, writeBps = 0; double? busyPct = null, queue = null, readLatMs = null, writeLatMs = null, readIops = null, writeIops = null;
            try { readBps = (long)(SafeNext(_readTotal) ?? 0); } catch { }
            try { writeBps = (long)(SafeNext(_writeTotal) ?? 0); } catch { }
            try { readIops = SafeNext(_readsTotal); } catch { }
            try { writeIops = SafeNext(_writesTotal); } catch { }
            try
            {
                var idle = SafeNext(_idlePctTotal);
                if (idle.HasValue) busyPct = Math.Clamp(100.0 - idle.Value, 0.0, 100.0);
            }
            catch { }
            try { queue = SafeNext(_queueTotal); } catch { }
            try { var v = SafeNext(_avgSecReadTotal); if (v.HasValue) readLatMs = v.Value * 1000.0; } catch { }
            try { var v = SafeNext(_avgSecWriteTotal); if (v.HasValue) writeLatMs = v.Value * 1000.0; } catch { }

            _lastTotals = (readBps, writeBps, busyPct, queue, readLatMs, writeLatMs, readIops, writeIops);
            _lastTicks = now;
            return new
            {
                read_bytes_per_sec = readBps,
                write_bytes_per_sec = writeBps,
                read_iops = readIops,
                write_iops = writeIops,
                busy_percent = busyPct,
                queue_length = queue,
                avg_read_latency_ms = readLatMs,
                avg_write_latency_ms = writeLatMs
            };
        }

        // 新增：读取每个 PhysicalDisk 实例
        public IReadOnlyList<object> ReadPerInstance()
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
                var inst = kv.Key;
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
                    disk_id = inst,
                    read_bytes_per_sec = rBps,
                    write_bytes_per_sec = wBps,
                    read_iops = rIops,
                    write_iops = wIops,
                    busy_percent = busyPct,
                    queue_length = queue,
                    avg_read_latency_ms = rLat,
                    avg_write_latency_ms = wLat
                });
            }
            _lastPerInst = list;
            _lastTicks = now;
            return list;
        }
    }
}
