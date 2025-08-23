using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using static SystemMonitor.Service.Services.Win32Interop;

namespace SystemMonitor.Service.Services
{
    internal sealed class ProcessDiskIoSampler
    {
        private static readonly Lazy<ProcessDiskIoSampler> _inst = new(() => new ProcessDiskIoSampler());
        public static ProcessDiskIoSampler Instance => _inst.Value;

        private readonly object _lock = new();
        private readonly Dictionary<int, (ulong r, ulong w)> _lastBytes = new();
        private long _lastTicks;
        private object[] _last = Array.Empty<object>();

        public object[] ReadTopByBytes(int topN)
        {
            var nowTicks = Environment.TickCount64;
            lock (_lock)
            {
                if (nowTicks - _lastTicks < 800)
                {
                    return _last;
                }
            }

            var results = new List<(int pid, string name, double rps, double wps)>();

            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { procs = Array.Empty<Process>(); }

            double intervalSec;
            lock (_lock)
            {
                var dtMs = Math.Max(200, nowTicks - _lastTicks);
                intervalSec = dtMs / 1000.0;
                if (_lastTicks == 0) intervalSec = 1.0; // first run, avoid div by zero; will yield 0 deltas
            }

            foreach (var p in procs)
            {
                try
                {
                    var pid = p.Id;
                    var name = string.Empty;
                    try { name = string.IsNullOrWhiteSpace(p.ProcessName) ? "(unknown)" : p.ProcessName; } catch { name = "(unknown)"; }

                    // Open process handle with limited rights
                    var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (h == IntPtr.Zero)
                    {
                        // As a fallback, try PROCESS_QUERY_LIMITED_INFORMATION may still fail for protected/system processes
                        continue;
                    }

                    try
                    {
                        if (!GetProcessIoCounters(h, out IO_COUNTERS io)) continue;
                        var readBytes = io.ReadTransferCount;
                        var writeBytes = io.WriteTransferCount;

                        ulong prevR = 0, prevW = 0;
                        lock (_lock)
                        {
                            _lastBytes.TryGetValue(pid, out var prev);
                            prevR = prev.r; prevW = prev.w;
                            _lastBytes[pid] = (readBytes, writeBytes);
                        }

                        // Handle wrap-around (unlikely with 64-bit) and first-sample
                        var dR = readBytes >= prevR ? (readBytes - prevR) : 0UL;
                        var dW = writeBytes >= prevW ? (writeBytes - prevW) : 0UL;

                        var rps = dR / Math.Max(0.001, intervalSec);
                        var wps = dW / Math.Max(0.001, intervalSec);

                        if (rps > 1 || wps > 1)
                        {
                            results.Add((pid, name, rps, wps));
                        }
                    }
                    finally
                    {
                        try { CloseHandle(h); } catch { }
                    }
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }

            var top = results
                .OrderByDescending(x => x.rps + x.wps)
                .ThenBy(x => x.pid)
                .Take(Math.Max(1, topN))
                .Select(x => (object)new
                {
                    pid = x.pid,
                    name = x.name,
                    read_bytes_per_sec = Math.Round(x.rps, 0),
                    write_bytes_per_sec = Math.Round(x.wps, 0)
                })
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
