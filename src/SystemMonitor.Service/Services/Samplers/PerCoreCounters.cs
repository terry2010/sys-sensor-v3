using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;

namespace SystemMonitor.Service.Services
{
    internal sealed class PerCoreCounters
    {
        private static readonly Lazy<PerCoreCounters> _inst = new(() => new PerCoreCounters());
        public static PerCoreCounters Instance => _inst.Value;
        private PerformanceCounter[]? _cores;
        private long _lastTicks;
        private double[] _last = Array.Empty<double>();
        private bool _initTried;
        private void EnsureInit()
        {
            if (_initTried) return; _initTried = true;
            try
            {
                var cat = new PerformanceCounterCategory("Processor");
                var names = cat.GetInstanceNames();
                var coreNames = names.Where(n => !string.Equals(n, "_Total", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n).ToArray();
                var list = new List<PerformanceCounter>();
                foreach (var n in coreNames)
                {
                    try { list.Add(new PerformanceCounter("Processor", "% Processor Time", n, readOnly: true)); } catch { }
                }
                _cores = list.ToArray();
                if (_cores != null) foreach (var c in _cores) { try { _ = c.NextValue(); } catch { } }
                _last = new double[_cores?.Length ?? 0];
                _lastTicks = Environment.TickCount64;
            } catch { }
        }
        public double[] Read()
        {
            EnsureInit(); var now = Environment.TickCount64;
            if (now - _lastTicks < 500) return _last;
            if (_cores == null || _cores.Length == 0) { _last = Array.Empty<double>(); _lastTicks = now; return _last; }
            var vals = new double[_cores.Length];
            for (int i = 0; i < _cores.Length; i++)
            {
                try { vals[i] = Math.Clamp(_cores[i].NextValue(), 0.0f, 100.0f); } catch { vals[i] = 0.0; }
            }
            _last = vals; _lastTicks = now; return _last;
        }
    }
}
