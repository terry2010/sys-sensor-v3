using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SystemMonitor.Service.Services.Collectors
{
    // IOCTL 骨架：阶段A 实现 USB 速率简单启发式；其余总线保留占位
    internal static class StorageIoctlHelper
    {
        // 对外入口：尝试获取协商链路速率字符串；失败返回 null
        // 参数：physicalDriveIndex 例如 0 表示 \\.\PHYSICALDRIVE0；iface: "nvme"|"sata"|"usb"|"scsi"|null
        //       model/pnp 可用于 USB 的启发式判断
        public static string? TryGetNegotiatedLinkSpeed(int physicalDriveIndex, string? iface, string? model = null, string? pnp = null)
        {
            try
            {
                // USB 简单启发式（不保证准确，仅阶段A占位）：
                if (string.Equals(iface, "usb", StringComparison.OrdinalIgnoreCase))
                {
                    var text = ($"{model} {pnp}")?.ToUpperInvariant() ?? string.Empty;
                    // 明确版本优先判断
                    if (text.Contains("USB 3.2") || text.Contains("GEN2X2")) return "20 Gbps";   // 3.2 Gen2x2 最高 20Gbps（仅粗略）
                    if (text.Contains("USB 3.1")) return "10 Gbps";   // 3.1 Gen2
                    if (text.Contains("USB3.1")) return "10 Gbps";
                    // SuperSpeed+（常见写法：SuperSpeed+、SS+、SS 10、SS10、SSP）
                    if (text.Contains("SUPERSPEED+") || text.Contains("SS+") || text.Contains("SS 10") || text.Contains("SS10") || text.Contains("SSP") || text.Contains("GEN2"))
                        return "10 Gbps";
                    // SuperSpeed（常见写法：SuperSpeed、SS、SS 5、SS5）
                    if (text.Contains("USB 3.0") || text.Contains("USB3.0") || text.Contains("USB3") || text.Contains("UASP") || text.Contains(" SUPERSPEED") || text.Contains(" SS ") || text.EndsWith(" SS") || text.StartsWith("SS ") || text.Contains("SS 5") || text.Contains("SS5") || text.Contains("GEN1"))
                        return "5 Gbps"; // 3.0 Gen1
                    if (text.Contains("USB 2.0") || text.Contains("USB2.0")) return "480 Mbps";
                    // 未命中则未知
                    System.Diagnostics.Trace.WriteLine($"[StorageIoctlHelper] USB speed unknown by heuristic. model={model} pnp={pnp}");
                    return null;
                }

                // 其余总线：阶段A返回 null，占位（后续阶段B实现）
                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // 下面是即将用于实现的 P/Invoke 框架；暂时未调用，保留以便后续实现
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        private static SafeFileHandle OpenPhysicalDrive(int index)
        {
            var path = @$"\\.\PHYSICALDRIVE{index}";
            var handle = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Open {path} failed");
            }
            return handle;
        }
    }
}
