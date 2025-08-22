using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SystemMonitor.Service.Services
{
    internal sealed class TopProcSampler
    {
        private static readonly Lazy<TopProcSampler> _inst = new(() => new TopProcSampler());
        public static TopProcSampler Instance => _inst.Value;

        private readonly object _lock = new();
        private readonly Dictionary<int, TimeSpan> _lastCpu = new();
        private long _lastTicks;
        private object[] _last = Array.Empty<object>();

        public object[] Read(int topN)
        {
            var nowTicks = Environment.TickCount64;
            lock (_lock)
            {
                if (nowTicks - _lastTicks < 800)
                {
                    return _last;
                }
            }

            var sw = Stopwatch.StartNew();
            var logical = Math.Max(1, Environment.ProcessorCount);
            var items = new List<(string name, int pid, double cpu)>();
            TimeSpan? elapsedRef = null;

            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { procs = Array.Empty<Process>(); }

            foreach (var p in procs)
            {
                try
                {
                    var pid = p.Id;
                    var name = string.Empty;
                    try { name = string.IsNullOrWhiteSpace(p.ProcessName) ? "(unknown)" : p.ProcessName; } catch { name = "(unknown)"; }

                    var total = p.TotalProcessorTime;

                    TimeSpan prev;
                    lock (_lock)
                    {
                        _lastCpu.TryGetValue(pid, out prev);
                        _lastCpu[pid] = total;
                    }

                    if (!elapsedRef.HasValue)
                    {
                        var dtMs = Math.Max(200, nowTicks - _lastTicks);
                        elapsedRef = TimeSpan.FromMilliseconds(dtMs);
                    }

                    var delta = total - prev;
                    if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
                    var elapsed = elapsedRef!.Value;
                    if (elapsed.TotalMilliseconds <= 0) continue;
                    var pct = 100.0 * (delta.TotalMilliseconds / (elapsed.TotalMilliseconds * logical));
                    pct = Math.Clamp(pct, 0.0, 100.0);
                    if (pct > 0.01)
                    {
                        items.Add((name, pid, Math.Round(pct, 2)));
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }

            var top = items
                .OrderByDescending(i => i.cpu)
                .ThenBy(i => i.pid)
                .Take(Math.Max(1, topN))
                .Select(i => (object)new { name = i.name, pid = i.pid, cpu_percent = i.cpu })
                .ToArray();

            lock (_lock)
            {
                _last = top;
                _lastTicks = nowTicks;
            }
            return top;
        }
    }
}
