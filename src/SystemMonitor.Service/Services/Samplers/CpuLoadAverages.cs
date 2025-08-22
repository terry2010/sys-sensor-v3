using System;

namespace SystemMonitor.Service.Services
{
    internal sealed class CpuLoadAverages
    {
        private static readonly Lazy<CpuLoadAverages> _inst = new(() => new CpuLoadAverages());
        public static CpuLoadAverages Instance => _inst.Value;
        private double _l1, _l5, _l15;
        private long _lastTs;
        public (double l1, double l5, double l15) Update(double usagePercent)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var dtSec = Math.Max(0.05, (now - _lastTs) / 1000.0);
            var x = Math.Clamp(usagePercent / 100.0, 0.0, 1.0);
            double step(double last, double win) { var alpha = 1 - Math.Exp(-dtSec / win); return last + alpha * (x - last); }
            _l1 = step(_l1, 60.0); _l5 = step(_l5, 300.0); _l15 = step(_l15, 900.0);
            _lastTs = now;
            return (_l1 * 100.0, _l5 * 100.0, _l15 * 100.0);
        }
    }
}
