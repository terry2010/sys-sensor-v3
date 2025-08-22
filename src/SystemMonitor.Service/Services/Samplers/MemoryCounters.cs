using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;

namespace SystemMonitor.Service.Services.Samplers
{
    internal sealed class MemoryCounters
    {
        private static readonly Lazy<MemoryCounters> _inst = new(() => new MemoryCounters());
        public static MemoryCounters Instance => _inst.Value;

        private readonly object _lock = new();
        private long _lastTicks;

        // Cached values (MB / per-sec numbers)
        private (double? CacheMb,
                 double? CommitLimitMb,
                 double? CommitUsedMb,
                 double? CommitPercent,
                 double? SwapTotalMb,
                 double? SwapUsedMb,
                 double? PageReadsPerSec,
                 double? PageWritesPerSec,
                 double? PageFaultsPerSec,
                 double? CompressedMb,
                 double? PoolPagedMb,
                 double? PoolNonpagedMb,
                 double? StandbyCacheMb,
                 double? WorkingSetTotalMb) _last;

        // PerfCounters
        private PerformanceCounter? _cacheBytes;
        private PerformanceCounter? _commitLimit;
        private PerformanceCounter? _committedBytes;
        private PerformanceCounter? _pageReadsPerSec;
        private PerformanceCounter? _pageWritesPerSec;
        private PerformanceCounter? _pageFaultsPerSec;
        private PerformanceCounter? _compressedBytes;
        private PerformanceCounter? _poolPagedBytes;
        private PerformanceCounter? _poolNonpagedBytes;

        // Standby cache counters (some may not exist on some systems)
        private readonly List<PerformanceCounter> _standbyCounters = new();

        private bool _initTried;

        private void EnsureInit()
        {
            if (_initTried) return;
            _initTried = true;
            try
            {
                _cacheBytes       = new PerformanceCounter("Memory", "Cache Bytes", readOnly: true);
                _commitLimit      = new PerformanceCounter("Memory", "Commit Limit", readOnly: true);
                _committedBytes   = new PerformanceCounter("Memory", "Committed Bytes", readOnly: true);
                _pageReadsPerSec  = new PerformanceCounter("Memory", "Page Reads/sec", readOnly: true);
                _pageWritesPerSec = new PerformanceCounter("Memory", "Page Writes/sec", readOnly: true);
                _pageFaultsPerSec = new PerformanceCounter("Memory", "Page Faults/sec", readOnly: true);
                try { _compressedBytes = new PerformanceCounter("Memory", "Compressed Bytes", readOnly: true); } catch { /* not available */ }
                _poolPagedBytes     = new PerformanceCounter("Memory", "Pool Paged Bytes", readOnly: true);
                _poolNonpagedBytes  = new PerformanceCounter("Memory", "Pool Nonpaged Bytes", readOnly: true);

                // Standby cache related counters – add those that exist
                TryAddStandbyCounter("Standby Cache Normal Priority Bytes");
                TryAddStandbyCounter("Standby Cache Reserve Bytes");
                TryAddStandbyCounter("Standby Cache Core Bytes");

                // Prime counters
                SafeNextValue(_cacheBytes);
                SafeNextValue(_commitLimit);
                SafeNextValue(_committedBytes);
                SafeNextValue(_pageReadsPerSec);
                SafeNextValue(_pageWritesPerSec);
                SafeNextValue(_pageFaultsPerSec);
                SafeNextValue(_compressedBytes);
                SafeNextValue(_poolPagedBytes);
                SafeNextValue(_poolNonpagedBytes);
                foreach (var c in _standbyCounters) SafeNextValue(c);

                _lastTicks = Environment.TickCount64;
                _last = default;
            }
            catch
            {
                // Ignore init errors; all reads will be null-safe
            }
        }

        private void TryAddStandbyCounter(string counterName)
        {
            try
            {
                var c = new PerformanceCounter("Memory", counterName, readOnly: true);
                _standbyCounters.Add(c);
            }
            catch { /* ignore if not present */ }
        }

        private static float? SafeNextValue(PerformanceCounter? c)
        {
            try { return c != null ? c.NextValue() : null; } catch { return null; }
        }

        private static double? BytesToMb(float? bytes)
        {
            if (!bytes.HasValue) return null;
            return bytes.Value / 1024.0 / 1024.0;
        }

        private static double? BytesToMb(double? bytes)
        {
            if (!bytes.HasValue) return null;
            return bytes.Value / 1024.0 / 1024.0;
        }

        private (double? totalMb, double? usedMb) ReadSwapViaWmi()
        {
            try
            {
                double total = 0, used = 0;
                using var searcher = new ManagementObjectSearcher("SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage");
                using var results = searcher.Get();
                foreach (ManagementObject mo in results)
                {
                    // These are documented in MB already
                    try { total += Convert.ToDouble(mo["AllocatedBaseSize"] ?? 0); } catch { }
                    try { used += Convert.ToDouble(mo["CurrentUsage"] ?? 0); } catch { }
                }
                return (total, used);
            }
            catch
            {
                return (null, null);
            }
        }

        public (double? CacheMb,
                double? CommitLimitMb,
                double? CommitUsedMb,
                double? CommitPercent,
                double? SwapTotalMb,
                double? SwapUsedMb,
                double? PageReadsPerSec,
                double? PageWritesPerSec,
                double? PageFaultsPerSec,
                double? CompressedMb,
                double? PoolPagedMb,
                double? PoolNonpagedMb,
                double? StandbyCacheMb,
                double? WorkingSetTotalMb) Read()
        {
            EnsureInit();
            var now = Environment.TickCount64;
            lock (_lock)
            {
                if (now - _lastTicks < 200)
                {
                    return _last;
                }

                double? cacheMb         = BytesToMb(SafeNextValue(_cacheBytes));
                double? commitLimitMb   = BytesToMb(SafeNextValue(_commitLimit));
                double? committedMb     = BytesToMb(SafeNextValue(_committedBytes));
                double? commitPct       = (committedMb.HasValue && commitLimitMb.HasValue && commitLimitMb > 0)
                                            ? Math.Clamp(100.0 * committedMb.Value / commitLimitMb.Value, 0, 100) : (double?)null;

                // Swap via WMI first
                var (swapTotalMb, swapUsedMb) = ReadSwapViaWmi();

                // Fallbacks for swap not implemented in this first pass; leave null if WMI failed

                double? pageReads  = SafeNextValue(_pageReadsPerSec);
                double? pageWrites = SafeNextValue(_pageWritesPerSec);
                double? pageFaults = SafeNextValue(_pageFaultsPerSec);

                double? compressedMb = BytesToMb(SafeNextValue(_compressedBytes));

                double? poolPagedMb     = BytesToMb(SafeNextValue(_poolPagedBytes));
                double? poolNonpagedMb  = BytesToMb(SafeNextValue(_poolNonpagedBytes));

                double standbySumBytes = 0;
                foreach (var c in _standbyCounters)
                {
                    var v = SafeNextValue(c);
                    if (v.HasValue) standbySumBytes += v.Value;
                }
                double? standbyMb = standbySumBytes > 0 ? standbySumBytes / 1024.0 / 1024.0 : (double?)null;

                // WorkingSetTotalMb – not reliably available here; set null for now
                double? workingSetTotalMb = null;

                _last = (cacheMb, commitLimitMb, committedMb, commitPct, swapTotalMb, swapUsedMb,
                         pageReads, pageWrites, pageFaults, compressedMb, poolPagedMb, poolNonpagedMb, standbyMb, workingSetTotalMb);
                _lastTicks = now;
                return _last;
            }
        }
    }
}
