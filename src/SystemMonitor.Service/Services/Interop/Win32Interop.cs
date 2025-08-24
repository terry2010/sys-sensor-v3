using System;
using System.Runtime.InteropServices;

namespace SystemMonitor.Service.Services
{
    internal static class Win32Interop
    {
        // FILETIME for GetSystemTimes
        [StructLayout(LayoutKind.Sequential)]
        internal struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        // GlobalMemoryStatusEx
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // ==========================
        // Process I/O counters (per-process disk bytes)
        // ==========================

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS ioCounters);

        // ==========================
        // Power status (battery/ac)
        // ==========================

        /// <summary>
        /// Win32 SYSTEM_POWER_STATUS 结构，用于获取 AC 状态、电池百分比与估算时间。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;          // 0=离线, 1=在线, 255=未知
            public byte BatteryFlag;           // 位标志：1 高电量,2 低电量,4 危急,8 充电,128 无电池,255 未知
            public byte BatteryLifePercent;    // 0..100, 255=未知
            public byte SystemStatusFlag;      // 保留
            public int BatteryLifeTime;        // 剩余秒数，-1=未知
            public int BatteryFullLifeTime;    // 充满秒数，-1=未知
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
    }
}
