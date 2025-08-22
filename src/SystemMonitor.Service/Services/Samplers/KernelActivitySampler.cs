using System;
using System.Diagnostics;

namespace SystemMonitor.Service.Services
{
    internal sealed class KernelActivitySampler
    {
        private static readonly Lazy<KernelActivitySampler> _inst = new(() => new KernelActivitySampler());
        public static KernelActivitySampler Instance => _inst.Value;

        private readonly object _lock = new();
        private long _lastTicks;
        private (double? ctx, double? sysc, double? intr) _lastValues;
        private bool _initTried;

        private PerformanceCounter? _pcCtx;
        private PerformanceCounter? _pcSyscalls;
        private PerformanceCounter? _pcIntr;

        public (double? contextSwitchesPerSec, double? syscallsPerSec, double? interruptsPerSec) Read()
        {
            var now = Environment.TickCount64;
            lock (_lock)
            {
                if (now - _lastTicks < 200)
                {
                    return _lastValues;
                }

                EnsureInit();

                double? ctx = null, sysc = null, intr = null;
                try { if (_pcCtx != null) ctx = Math.Max(0, _pcCtx.NextValue()); } catch { ctx = null; }
                try { if (_pcSyscalls != null) sysc = Math.Max(0, _pcSyscalls.NextValue()); } catch { sysc = null; }
                try { if (_pcIntr != null) intr = Math.Max(0, _pcIntr.NextValue()); } catch { intr = null; }

                _lastValues = (ctx, sysc, intr);
                _lastTicks = now;
                return _lastValues;
            }
        }

        private void EnsureInit()
        {
            if (_initTried) return;
            _initTried = true;
            try
            {
                _pcCtx = TryCreateCounter("System", "Context Switches/sec");
                _pcSyscalls = TryCreateCounter("System", "System Calls/sec");
                _pcIntr = TryCreateCounter("System", "Interrupts/sec");

                if (_pcCtx == null)
                    _pcCtx = TryCreateCounter("Processor Information", "Context Switches/sec", "_Total");
                if (_pcSyscalls == null)
                    _pcSyscalls = TryCreateCounter("Processor Information", "System Calls/sec", "_Total");
                if (_pcIntr == null)
                    _pcIntr = TryCreateCounter("Processor Information", "Interrupts/sec", "_Total");
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
