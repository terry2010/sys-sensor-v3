using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class NetCounters
    {
        private static readonly Lazy<NetCounters> _inst = new(() => new NetCounters());
        public static NetCounters Instance => _inst.Value;

        private System.Diagnostics.PerformanceCounter[]? _sent;
        private System.Diagnostics.PerformanceCounter[]? _recv;
        private long _lastTicks;
        private (long up, long down) _last;
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
                var sent = new List<System.Diagnostics.PerformanceCounter>();
                var recv = new List<System.Diagnostics.PerformanceCounter>();
                foreach (var inst in valid)
                {
                    try
                    {
                        sent.Add(new System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Sent/sec", inst, readOnly: true));
                        recv.Add(new System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Received/sec", inst, readOnly: true));
                    }
                    catch { /* ignore this instance */ }
                }
                _sent = sent.ToArray();
                _recv = recv.ToArray();
                if (_sent != null) foreach (var c in _sent) { try { _ = c.NextValue(); } catch { } }
                if (_recv != null) foreach (var c in _recv) { try { _ = c.NextValue(); } catch { } }
                _lastTicks = Environment.TickCount64;
                _last = (0, 0);
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
            if (now - _lastTicks < 200)
            {
                return new { up_bytes_per_sec = _last.up, down_bytes_per_sec = _last.down };
            }
            long up = 0, down = 0;
            if (_sent != null)
            {
                foreach (var c in _sent)
                {
                    try { up += (long)c.NextValue(); } catch { }
                }
            }
            if (_recv != null)
            {
                foreach (var c in _recv)
                {
                    try { down += (long)c.NextValue(); } catch { }
                }
            }
            _last = (up, down);
            _lastTicks = now;
            return new { up_bytes_per_sec = up, down_bytes_per_sec = down };
        }
    }
}
