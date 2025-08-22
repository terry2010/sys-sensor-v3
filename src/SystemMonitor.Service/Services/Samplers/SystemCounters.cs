using System;
using System.Diagnostics;

namespace SystemMonitor.Service.Services
{
    internal sealed class SystemCounters
    {
        private static readonly Lazy<SystemCounters> _inst = new(() => new SystemCounters());
        public static SystemCounters Instance => _inst.Value;
        private PerformanceCounter? _proc;
        private PerformanceCounter? _threads;
        private long _lastTicks;
        private (int proc, int threads) _last;
        private bool _initTried;
        private void EnsureInit()
        {
            if (_initTried) return; _initTried = true;
            try
            {
                _proc = new PerformanceCounter("System", "Processes", readOnly: true);
                _threads = new PerformanceCounter("System", "Threads", readOnly: true);
                _ = _proc.NextValue(); _ = _threads.NextValue();
                _lastTicks = Environment.TickCount64; _last = (0, 0);
            } catch { }
        }
        public (int, int) ReadProcThread()
        {
            EnsureInit(); var now = Environment.TickCount64;
            if (now - _lastTicks < 500) return _last;
            int p = 0, t = 0; try { if (_proc != null) p = (int)_proc.NextValue(); } catch { }
            try { if (_threads != null) t = (int)_threads.NextValue(); } catch { }
            _last = (p, t); _lastTicks = now; return _last;
        }
    }
}
