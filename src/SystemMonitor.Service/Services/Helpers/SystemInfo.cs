using System;
using System.Linq;
using SystemMonitor.Service.Services;
using static SystemMonitor.Service.Services.Win32Interop;

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

        internal static bool TryGetMemoryStatus(out MEMORYSTATUSEX status)
        {
            status = new MEMORYSTATUSEX();
            status.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            return GlobalMemoryStatusEx(ref status);
        }
    }
}
