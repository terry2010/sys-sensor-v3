using System;
using System.Collections.Generic;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class DiskCounters
    {
        private static readonly Lazy<DiskCounters> _inst = new(() => new DiskCounters());
        public static DiskCounters Instance => _inst.Value;

        private System.Diagnostics.PerformanceCounter? _read;
        private System.Diagnostics.PerformanceCounter? _write;
        private System.Diagnostics.PerformanceCounter? _queue;
        private long _lastTicks;
        private (long read, long write, double queue) _last;
        private bool _initTried;

        private void EnsureInit()
        {
            if (_initTried) return;
            _initTried = true;
            try
            {
                _read = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", readOnly: true);
                _write = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", readOnly: true);
                _queue = new System.Diagnostics.PerformanceCounter("PhysicalDisk", "Current Disk Queue Length", "_Total", readOnly: true);
                _ = _read.NextValue();
                _ = _write.NextValue();
                _ = _queue.NextValue();
                _lastTicks = Environment.TickCount64;
                _last = (0, 0, 0.0);
            }
            catch
            {
                // ignore; 使用回退
            }
        }

        public object Read()
        {
            EnsureInit();
            var now = Environment.TickCount64;
            if (now - _lastTicks < 200)
            {
                return new { read_bytes_per_sec = _last.read, write_bytes_per_sec = _last.write, queue_length = _last.queue };
            }
            long r = 0, w = 0; double q = 0.0;
            try { if (_read != null) r = (long)_read.NextValue(); } catch { r = 0; }
            try { if (_write != null) w = (long)_write.NextValue(); } catch { w = 0; }
            try { if (_queue != null) q = _queue.NextValue(); } catch { q = 0.0; }
            _last = (r, w, q);
            _lastTicks = now;
            return new { read_bytes_per_sec = r, write_bytes_per_sec = w, queue_length = q };
        }
    }
}
