using System;
using System.Diagnostics;
using System.Linq;

namespace SystemMonitor.Service.Services
{
    internal sealed class PerCoreFrequency
    {
        private static readonly Lazy<PerCoreFrequency> _inst = new(() => new PerCoreFrequency());
        public static PerCoreFrequency Instance => _inst.Value;

        private PerformanceCounter[]? _perfPct;
        private long _lastTicks;
        private int?[] _last = Array.Empty<int?>();
        private bool _initTried;

        private void EnsureInit()
        {
            if (_initTried) return; _initTried = true;
            try
            {
                var cat = new PerformanceCounterCategory("Processor Information");
                var instances = cat.GetInstanceNames()
                    .Where(n => !string.Equals(n, "_Total", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                _perfPct = instances
                    .Select(n => new PerformanceCounter("Processor Information", "% Processor Performance", n, readOnly: true))
                    .ToArray();
                foreach (var c in _perfPct) { try { _ = c.NextValue(); } catch { } }
                _lastTicks = Environment.TickCount64;
                _last = new int?[instances.Length];
            }
            catch
            {
                _perfPct = Array.Empty<PerformanceCounter>();
                _last = Array.Empty<int?>();
            }
        }

        public int?[] Read()
        {
            EnsureInit();
            var now = Environment.TickCount64;
            if (now - _lastTicks < 500) return _last;

            var (_, maxMHz) = CpuFrequency.Instance.Read();
            if (_perfPct == null || _perfPct.Length == 0 || !maxMHz.HasValue)
            {
                _lastTicks = now; return _last;
            }
            var arr = new int?[_perfPct.Length];
            for (int i = 0; i < _perfPct.Length; i++)
            {
                try
                {
                    var pct = Math.Clamp(_perfPct[i].NextValue(), 0.0f, 200.0f);
                    arr[i] = (int)Math.Max(0, Math.Round(maxMHz.Value * (pct / 100.0)));
                }
                catch { arr[i] = null; }
            }
            _last = arr; _lastTicks = now; return _last;
        }
    }
}
