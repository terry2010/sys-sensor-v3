using System;
using System.Diagnostics;

namespace SystemMonitor.Service.Services
{
    internal sealed class CpuFrequency
    {
        private static readonly Lazy<CpuFrequency> _inst = new(() => new CpuFrequency());
        public static CpuFrequency Instance => _inst.Value;
        private long _lastTicks;
        private (int? cur, int? max) _last;
        private bool _initTried;
        private PerformanceCounter? _pcFreq;
        private PerformanceCounter? _pcPerfPct;
        private int? _busMhz;
        private int? _minMhz;
        public (int? cur, int? max) Read()
        {
            var now = Environment.TickCount64;
            if (now - _lastTicks < 1_000) return _last;
            EnsureInit();
            int? cur = null, max = null;
            try { if (_pcFreq != null) cur = Math.Max(0, Convert.ToInt32(_pcFreq.NextValue())); } catch { cur = null; }
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor");
                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    try { var wCur = Convert.ToInt32(obj["CurrentClockSpeed"]); cur = cur ?? Math.Max(0, wCur); } catch { }
                    try { var wMax = Convert.ToInt32(obj["MaxClockSpeed"]); max = Math.Max(max ?? 0, wMax); } catch { }
                }
            }
            catch { }
            if (cur == null)
            {
                try
                {
                    if (_pcPerfPct != null)
                    {
                        var pct = Math.Max(0.0f, Math.Min(100.0f, _pcPerfPct.NextValue()));
                        if (max.HasValue)
                        {
                            cur = (int)Math.Max(0, Math.Round(max.Value * (pct / 100.0)));
                        }
                    }
                }
                catch { }
            }
            _last = (cur, max); _lastTicks = now; return _last;
        }
        public int? ReadBusMhz()
        {
            if (_busMhz.HasValue) return _busMhz;
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT ExtClock FROM Win32_Processor");
                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    try { var v = Convert.ToInt32(obj["ExtClock"]); if (v > 0) { _busMhz = v; break; } } catch { }
                }
            }
            catch { }
            return _busMhz;
        }
        public int? ReadMinMhz()
        {
            if (_minMhz.HasValue) return _minMhz;
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT MinClockSpeed FROM Win32_Processor");
                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    try { var v = Convert.ToInt32(obj["MinClockSpeed"]); if (v > 0) { _minMhz = v; break; } } catch { }
                }
            }
            catch { }
            return _minMhz;
        }
        private void EnsureInit()
        {
            if (_initTried) return; _initTried = true;
            try
            {
                _pcFreq = TryCreateCounter("Processor Information", "Processor Frequency", "_Total");
                if (_pcFreq == null)
                    _pcFreq = TryCreateCounter("Processor", "Processor Frequency", "_Total");
                _pcPerfPct = TryCreateCounter("Processor Information", "% Processor Performance", "_Total");
            }
            catch { }
        }
        private static PerformanceCounter? TryCreateCounter(string category, string counter, string? instance = null)
        {
            try
            {
                if (instance == null)
                    return new PerformanceCounter(category, counter, readOnly: true);
                else
                    return new PerformanceCounter(category, counter, instance, readOnly: true);
            }
            catch { return null; }
        }
    }
}
