using System;
using System.Linq;
using SystemMonitor.Service.Services;
using static SystemMonitor.Service.Services.Win32Interop;
using SystemMonitor.Service.Services.Samplers;

namespace SystemMonitor.Service.Services
{
    internal static class SystemInfo
    {
        private static readonly object _cpuLock = new();
        private static bool _cpuInit;
        private static ulong _prevIdle, _prevKernel, _prevUser;

        private static readonly object _cpuBkLock = new();
        private static bool _cpuBkInit;
        private static ulong _bkPrevIdle, _bkPrevKernel, _bkPrevUser;

        internal static double GetCpuUsagePercent()
        {
            try
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user))
                {
                    return 0.0;
                }
                static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
                var idleNow = ToUInt64(idle);
                var kernelNow = ToUInt64(kernel);
                var userNow = ToUInt64(user);
                double usage = 0.0;
                lock (_cpuLock)
                {
                    if (_cpuInit)
                    {
                        var idleDelta = idleNow - _prevIdle;
                        var kernelDelta = kernelNow - _prevKernel;
                        var userDelta = userNow - _prevUser;
                        var total = kernelDelta + userDelta; // kernel 包含 idle
                        var busy = total > idleDelta ? (total - idleDelta) : 0UL;
                        if (total > 0)
                        {
                            usage = Math.Clamp(100.0 * busy / total, 0.0, 100.0);
                        }
                    }
                    _prevIdle = idleNow;
                    _prevKernel = kernelNow;
                    _prevUser = userNow;
                    _cpuInit = true;
                }
                return usage;
            }
            catch
            {
                return 0.0;
            }
        }

        internal static (double user, double sys, double idle) GetCpuBreakdownPercent()
        {
            try
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user)) return (0, 0, 0);
                static ulong ToU64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
                var iNow = ToU64(idle); var kNow = ToU64(kernel); var uNow = ToU64(user);
                double u = 0, s = 0, id = 0;
                lock (_cpuBkLock)
                {
                    if (_cpuBkInit)
                    {
                        var iD = iNow - _bkPrevIdle;
                        var kD = kNow - _bkPrevKernel;
                        var uD = uNow - _bkPrevUser;
                        var total = kD + uD;
                        if (total > 0)
                        {
                            var busy = total > iD ? (total - iD) : 0UL;
                            var kBusy = kD > iD ? (kD - iD) : 0UL;
                            u = 100.0 * uD / total;
                            s = 100.0 * kBusy / total;
                            var used = busy;
                            id = 100.0 * (total - used) / total;
                            u = Math.Clamp(u, 0.0, 100.0);
                            s = Math.Clamp(s, 0.0, 100.0);
                            id = Math.Clamp(id, 0.0, 100.0);
                        }
                    }
                    _bkPrevIdle = iNow; _bkPrevKernel = kNow; _bkPrevUser = uNow; _cpuBkInit = true;
                }
                return (u, s, id);
            }
            catch { return (0, 0, 0); }
        }

        internal static (long total_mb, long used_mb) GetMemoryInfoMb()
        {
            try
            {
                if (TryGetMemoryStatus(out var status))
                {
                    long totalMb = (long)(status.ullTotalPhys / (1024 * 1024));
                    long availMb = (long)(status.ullAvailPhys / (1024 * 1024));
                    long usedMb = Math.Max(0, totalMb - availMb);
                    return (totalMb, usedMb);
                }
            }
            catch
            {
                // ignore
            }
            return (16_000, 8_000);
        }

        // DTO for memory details (serialized with snake_case by System.Text.Json policy)
        internal sealed class MemoryDetail
        {
            public long? TotalMb { get; init; }
            public long? UsedMb { get; init; }
            public long? AvailableMb { get; init; }
            public double? PercentUsed { get; init; }

            public long? CachedMb { get; init; }

            public long? CommitLimitMb { get; init; }
            public long? CommitUsedMb { get; init; }
            public double? CommitPercent { get; init; }

            public long? SwapTotalMb { get; init; }
            public long? SwapUsedMb { get; init; }

            public double? PagesInPerSec { get; init; }
            public double? PagesOutPerSec { get; init; }
            public double? PageFaultsPerSec { get; init; }

            public long? CompressedBytesMb { get; init; }
            public long? PoolPagedMb { get; init; }
            public long? PoolNonpagedMb { get; init; }
            public long? StandbyCacheMb { get; init; }
            public long? WorkingSetTotalMb { get; init; }

            public double? MemoryPressurePercent { get; init; }
            public string? MemoryPressureLevel { get; init; }
        }

        internal static MemoryDetail GetMemoryDetail()
        {
            long? totalMb = null, availMb = null, usedMb = null;
            double? percentUsed = null;
            try
            {
                if (TryGetMemoryStatus(out var status))
                {
                    totalMb = (long)(status.ullTotalPhys / (1024 * 1024));
                    availMb = (long)(status.ullAvailPhys / (1024 * 1024));
                    if (totalMb.HasValue && availMb.HasValue)
                    {
                        usedMb = Math.Max(0, totalMb.Value - availMb.Value);
                        if (totalMb.Value > 0)
                        {
                            percentUsed = Math.Clamp(100.0 * usedMb.Value / (double)totalMb.Value, 0.0, 100.0);
                        }
                    }
                }
            }
            catch { /* ignore */ }

            var mc = MemoryCounters.Instance.Read();

            static long? ToLongMb(double? v) => v.HasValue ? (long?)Math.Round(v.Value) : null;

            var cachedMb        = ToLongMb(mc.CacheMb);
            var commitLimitMb   = ToLongMb(mc.CommitLimitMb);
            var commitUsedMb    = ToLongMb(mc.CommitUsedMb);
            var commitPercent   = mc.CommitPercent.HasValue ? Math.Clamp(mc.CommitPercent.Value, 0.0, 100.0) : (double?)null;

            var swapTotalMb     = ToLongMb(mc.SwapTotalMb);
            var swapUsedMb      = ToLongMb(mc.SwapUsedMb);

            var pagesIn         = mc.PageReadsPerSec;
            var pagesOut        = mc.PageWritesPerSec;
            var pageFaults      = mc.PageFaultsPerSec;

            var compressedMb    = ToLongMb(mc.CompressedMb);
            var poolPagedMb     = ToLongMb(mc.PoolPagedMb);
            var poolNonpagedMb  = ToLongMb(mc.PoolNonpagedMb);
            var standbyCacheMb  = ToLongMb(mc.StandbyCacheMb);
            var workingSetMb    = ToLongMb(mc.WorkingSetTotalMb);

            // Memory pressure
            double? pressure = null; string? level = null;
            var candidates = new[] { percentUsed, commitPercent }.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            if (candidates.Length > 0)
            {
                pressure = candidates.Max();
                level = pressure < 70.0 ? "green" : (pressure <= 90.0 ? "yellow" : "red");
            }

            return new MemoryDetail
            {
                TotalMb = totalMb,
                UsedMb = usedMb,
                AvailableMb = availMb,
                PercentUsed = percentUsed,

                CachedMb = cachedMb,

                CommitLimitMb = commitLimitMb,
                CommitUsedMb = commitUsedMb,
                CommitPercent = commitPercent,

                SwapTotalMb = swapTotalMb,
                SwapUsedMb = swapUsedMb,

                PagesInPerSec = pagesIn,
                PagesOutPerSec = pagesOut,
                PageFaultsPerSec = pageFaults,

                CompressedBytesMb = compressedMb,
                PoolPagedMb = poolPagedMb,
                PoolNonpagedMb = poolNonpagedMb,
                StandbyCacheMb = standbyCacheMb,
                WorkingSetTotalMb = workingSetMb,

                MemoryPressurePercent = pressure,
                MemoryPressureLevel = level
            };
        }

        internal static bool TryGetMemoryStatus(out MEMORYSTATUSEX status)
        {
            status = new MEMORYSTATUSEX();
            status.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            return GlobalMemoryStatusEx(ref status);
        }
    }
}
