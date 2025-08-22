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

        // 读取 NVMe Identify（Controller）字符串（序列号/型号/固件），失败返回 null
        public static (string serial, string model, string firmware)? TryReadNvmeIdentifyStrings(int physicalDriveIndex)
        {
            try
            {
                using var h = OpenPhysicalDrive(physicalDriveIndex);
                var query = new STORAGE_PROPERTY_QUERY
                {
                    PropertyId = STORAGE_PROPERTY_ID.StorageDeviceProtocolSpecificProperty,
                    QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery
                };
                var proto = new STORAGE_PROTOCOL_SPECIFIC_DATA
                {
                    ProtocolType = STORAGE_PROTOCOL_TYPE.ProtocolTypeNvme,
                    DataType = (uint)STORAGE_PROTOCOL_NVME_DATA_TYPE.NVMeDataTypeIdentify,
                    ProtocolDataRequestValue = 0x02, // CNS = 0x02 -> Identify Controller
                    ProtocolDataRequestSubValue = 0, // NamespaceId not used for controller
                    ProtocolDataOffset = (uint)Marshal.SizeOf<STORAGE_PROTOCOL_DATA_DESCRIPTOR>(),
                    ProtocolDataLength = 4096
                };

                int inSize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>() + Marshal.SizeOf<STORAGE_PROTOCOL_SPECIFIC_DATA>();
                int outSize = Marshal.SizeOf<STORAGE_PROTOCOL_DATA_DESCRIPTOR>() + (int)proto.ProtocolDataLength;

                var inBuf = Marshal.AllocHGlobal(inSize);
                var outBuf = Marshal.AllocHGlobal(outSize);
                try
                {
                    Marshal.StructureToPtr(query, inBuf, false);
                    Marshal.StructureToPtr(proto, (IntPtr)(inBuf.ToInt64() + Marshal.SizeOf<STORAGE_PROPERTY_QUERY>()), false);

                    if (!DeviceIoControl(h,
                        IOCTL_STORAGE_QUERY_PROPERTY,
                        inBuf, (uint)inSize,
                        outBuf, (uint)outSize,
                        out _,
                        IntPtr.Zero))
                    {
                        return null;
                    }

                    var desc = Marshal.PtrToStructure<STORAGE_PROTOCOL_DATA_DESCRIPTOR>(outBuf);
                    var sp = desc.ProtocolSpecific;
                    if (sp.ProtocolDataLength < 512 || sp.ProtocolDataOffset == 0)
                        return null;

                    var dataPtr = (IntPtr)(outBuf.ToInt64() + (int)sp.ProtocolDataOffset);
                    // NVMe Identify Controller 字段（ASCII, space-padded）：
                    // SN @ 4..23 (20 bytes), MN @ 24..63 (40 bytes), FR @ 64..71 (8 bytes)
                    static string ReadAsciiTrim(IntPtr basePtr, int offset, int len)
                    {
                        var bytes = new byte[len];
                        Marshal.Copy((IntPtr)(basePtr.ToInt64() + offset), bytes, 0, len);
                        var s = System.Text.Encoding.ASCII.GetString(bytes);
                        return s.Trim('\0', ' ');
                    }
                    string sn = ReadAsciiTrim(dataPtr, 4, 20);
                    string mn = ReadAsciiTrim(dataPtr, 24, 40);
                    string fr = ReadAsciiTrim(dataPtr, 64, 8);
                    if (string.IsNullOrWhiteSpace(sn) && string.IsNullOrWhiteSpace(mn) && string.IsNullOrWhiteSpace(fr))
                        return null;
                    return (sn, mn, fr);
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuf);
                    Marshal.FreeHGlobal(outBuf);
                }
            }
            catch { return null; }
        }

        // 阶段B：原生 SMART 读取占位（后续填充 IOCTL 逻辑）
        internal sealed class SmartSummary
        {
            public string? overall_health { get; set; }
            public double? temperature_c { get; set; }
            public double? power_on_hours { get; set; }
            public double? reallocated_sector_count { get; set; }
            public double? pending_sector_count { get; set; }
            public double? udma_crc_error_count { get; set; }
            public double? nvme_percentage_used { get; set; }
            public double? nvme_data_units_read { get; set; }
            public double? nvme_data_units_written { get; set; }
            public double? nvme_controller_busy_time_min { get; set; }
            public double? nvme_power_cycles { get; set; }
            public double? nvme_power_on_hours { get; set; }
            public double? unsafe_shutdowns { get; set; }
            public double? nvme_media_errors { get; set; }
            public double? nvme_available_spare { get; set; }
            public double? nvme_spare_threshold { get; set; }
            public byte? nvme_critical_warning { get; set; }
            public double? nvme_temp_sensor1_c { get; set; }
            public double? nvme_temp_sensor2_c { get; set; }
            public double? nvme_temp_sensor3_c { get; set; }
            public double? nvme_temp_sensor4_c { get; set; }
            public double? thermal_throttle_events { get; set; }
        }

        // SATA/ATA SMART（占位实现：返回 null）
        public static SmartSummary? TryReadAtaSmartSummary(int physicalDriveIndex)
        {
            try
            {
                using var h = OpenPhysicalDrive(physicalDriveIndex);

                // 先查询 SMART 版本，确认支持
                int verSize = Marshal.SizeOf<GETVERSIONINPARAMS>();
                var verBuf = Marshal.AllocHGlobal(verSize);
                try
                {
                    if (!DeviceIoControl(h, SMART_GET_VERSION, IntPtr.Zero, 0, verBuf, (uint)verSize, out _, IntPtr.Zero))
                    {
                        return null;
                    }
                    var ver = Marshal.PtrToStructure<GETVERSIONINPARAMS>(verBuf);
                    if ((ver.fCapabilities & CAP_SMART_CMD) == 0)
                    {
                        return null;
                    }
                }
                finally { Marshal.FreeHGlobal(verBuf); }

                // 读取 SMART Attributes（使用 READ_ATTRIBUTES 命令）
                int inSize = Marshal.SizeOf<SENDCMDINPARAMS>() - 1; // bBuffer[1] 已包含在结构末尾
                int outSize = Marshal.SizeOf<SENDCMDOUTPARAMS>() + 512; // 512 字节数据区
                var inBuf = Marshal.AllocHGlobal(inSize);
                var outBuf = Marshal.AllocHGlobal(outSize);
                try
                {
                    var inParams = new SENDCMDINPARAMS();
                    inParams.cBufferSize = 512;
                    inParams.irDriveRegs.bFeaturesReg = SMART_READ_DATA; // 0xD0
                    inParams.irDriveRegs.bSectorCountReg = 1;
                    inParams.irDriveRegs.bSectorNumberReg = 1;
                    inParams.irDriveRegs.bCylLowReg = 0x4F;
                    inParams.irDriveRegs.bCylHighReg = 0xC2;
                    inParams.irDriveRegs.bDriveHeadReg = 0xA0; // 主盘位，现代驱动忽略
                    inParams.irDriveRegs.bCommandReg = SMART_CMD; // 0xB0
                    Marshal.StructureToPtr(inParams, inBuf, false);

                    if (!DeviceIoControl(h, SMART_RCV_DRIVE_DATA, inBuf, (uint)inSize, outBuf, (uint)outSize, out _, IntPtr.Zero))
                    {
                        return null;
                    }

                    // 从 SENDCMDOUTPARAMS 末尾的 512 字节缓冲读取属性表
                    var dataPtr = (IntPtr)(outBuf.ToInt64() + Marshal.SizeOf<SENDCMDOUTPARAMS>());
                    byte[] smartData = new byte[512];
                    Marshal.Copy(dataPtr, smartData, 0, 512);

                    // 解析 30 个条目，每条 12 字节，从偏移 2 开始
                    double? tempC = null, poh = null, realloc = null, pending = null, crc = null;
                    for (int i = 2; i < 2 + 12 * 30; i += 12)
                    {
                        byte id = smartData[i];
                        if (id == 0) continue;
                        // 原始值 6 字节位于偏移 i+5..i+10（LE）
                        ulong raw = 0;
                        for (int b = 0; b < 6; b++) raw |= ((ulong)smartData[i + 5 + b]) << (8 * b);

                        switch (id)
                        {
                            case 0x09: // Power-On Hours
                                poh = (double)raw;
                                break;
                            case 0x05: // Reallocated Sector Count
                                realloc = (double)raw;
                                break;
                            case 0xC5: // Current Pending Sector Count
                                pending = (double)raw;
                                break;
                            case 0xC7: // UDMA CRC Error Count
                                crc = (double)raw;
                                break;
                            case 0xC2: // Temperature (value 字节通常在 i+3；raw 亦可能包含摄氏)
                                // 优先取 Value 字节（常为摄氏温度）
                                byte val = smartData[i + 3];
                                if (val > 0 && val < 125) tempC = val; else tempC = (double)raw;
                                break;
                        }
                    }

                    return new SmartSummary
                    {
                        temperature_c = tempC,
                        power_on_hours = poh,
                        reallocated_sector_count = realloc,
                        pending_sector_count = pending,
                        udma_crc_error_count = crc
                    };
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuf);
                    Marshal.FreeHGlobal(outBuf);
                }
            }
            catch { return null; }
        }

        // NVMe SMART/Health
        public static SmartSummary? TryReadNvmeSmartSummary(int physicalDriveIndex)
        {
            try
            {
                using var h = OpenPhysicalDrive(physicalDriveIndex);
                // 构造 NVMe 协议特定查询（Health Log Page 0x02）
                var query = new STORAGE_PROPERTY_QUERY
                {
                    PropertyId = STORAGE_PROPERTY_ID.StorageDeviceProtocolSpecificProperty,
                    QueryType = STORAGE_QUERY_TYPE.PropertyStandardQuery
                };

                var proto = new STORAGE_PROTOCOL_SPECIFIC_DATA
                {
                    ProtocolType = STORAGE_PROTOCOL_TYPE.ProtocolTypeNvme,
                    DataType = (uint)STORAGE_PROTOCOL_NVME_DATA_TYPE.NVMeDataTypeLogPage,
                    ProtocolDataRequestValue = 0x02, // Health Information Log
                    ProtocolDataRequestSubValue = 0,
                    ProtocolDataOffset = (uint)Marshal.SizeOf<STORAGE_PROTOCOL_DATA_DESCRIPTOR>(),
                    ProtocolDataLength = 512
                };

                int inSize = Marshal.SizeOf<STORAGE_PROPERTY_QUERY>() + Marshal.SizeOf<STORAGE_PROTOCOL_SPECIFIC_DATA>();
                int outSize = Marshal.SizeOf<STORAGE_PROTOCOL_DATA_DESCRIPTOR>() + (int)proto.ProtocolDataLength;

                var inBuf = Marshal.AllocHGlobal(inSize);
                var outBuf = Marshal.AllocHGlobal(outSize);
                try
                {
                    // 将 query 与 proto 连续写入输入缓冲
                    Marshal.StructureToPtr(query, inBuf, false);
                    Marshal.StructureToPtr(proto, (IntPtr)(inBuf.ToInt64() + Marshal.SizeOf<STORAGE_PROPERTY_QUERY>()), false);

                    if (!DeviceIoControl(h,
                        IOCTL_STORAGE_QUERY_PROPERTY,
                        inBuf, (uint)inSize,
                        outBuf, (uint)outSize,
                        out uint returned,
                        IntPtr.Zero))
                    {
                        return null;
                    }

                    // 解析输出：STORAGE_PROTOCOL_DATA_DESCRIPTOR + 数据区
                    var desc = Marshal.PtrToStructure<STORAGE_PROTOCOL_DATA_DESCRIPTOR>(outBuf);
                    if (desc.Version != (uint)Marshal.SizeOf<STORAGE_PROTOCOL_DATA_DESCRIPTOR>() ||
                        desc.Size != (uint)Marshal.SizeOf<STORAGE_PROTOCOL_DATA_DESCRIPTOR>())
                    {
                        return null;
                    }

                    var sp = desc.ProtocolSpecific;
                    if (sp.ProtocolDataLength < 512 || sp.ProtocolDataOffset == 0)
                    {
                        return null;
                    }

                    var dataPtr = (IntPtr)(outBuf.ToInt64() + (int)sp.ProtocolDataOffset);
                    // NVMe SMART/Health Log 布局（NVMe 1.x 常见）：
                    // 0 Critical Warning, 1..2 Composite Temp (K), 3 Avail Spare, 4 Avail Spare Threshold, 5 Percentage Used
                    // 32..47 Data Units Read, 48..63 Data Units Written, 64..79 Host Read Cmds, 80..95 Host Write Cmds,
                    // 96..111 Controller Busy Time (min), 112..127 Power Cycles, 128..143 Power On Hours,
                    // 144..159 Unsafe Shutdowns, 160..175 Media Errors, 176..191 Number of Error Info Entries,
                    // 192..195 Warning Temp Time, 196..199 Critical Temp Time,
                    // 200..219 Temperature Sensors 1..8 (2 bytes each, K)

                    byte crit = Marshal.ReadByte(dataPtr, 0);
                    byte availSpare = Marshal.ReadByte(dataPtr, 3);
                    byte spareTh = Marshal.ReadByte(dataPtr, 4);
                    ushort tempRaw = ReadUInt16(dataPtr, 1);
                    double? tempC = null;
                    if (tempRaw > 0)
                    {
                        tempC = tempRaw > 273 ? (double)(tempRaw - 273) : (double)tempRaw;
                    }

                    // Percentage Used (byte 5)
                    byte used = Marshal.ReadByte(dataPtr, 5);
                    double? pctUsed = used <= 100 ? (double)used : (double)(used & 0x7F); // 简单裁剪

                    // Data Units Read/Written (bytes 32..47, 48..63) 每单位 = 512,000 bytes per NVMe spec（有的实现 1000*512）
                    // 我们先读取为 64-bit（低 8 字节），并换算为 GB（十进制）。
                    ulong dur = ReadUInt64(dataPtr, 32);
                    ulong duw = ReadUInt64(dataPtr, 48);
                    // 转换：单位= 512,000 bytes（规范定义为 1000 * 512）
                    const double BYTES_PER_DATA_UNIT = 512000.0;
                    double dataReadGB = dur * BYTES_PER_DATA_UNIT / 1_000_000_000.0;
                    double dataWriteGB = duw * BYTES_PER_DATA_UNIT / 1_000_000_000.0;

                    // Controller Busy Time (minutes) bytes 96..111（取低 8 字节）
                    ulong busyMin = ReadUInt64(dataPtr, 96);
                    // Power Cycles bytes 112..127
                    ulong powerCycles = ReadUInt64(dataPtr, 112);
                    // Power On Hours bytes 128..143
                    ulong poh = ReadUInt64(dataPtr, 128);
                    // Unsafe Shutdowns bytes 144..159
                    ulong unsafeCnt = ReadUInt64(dataPtr, 144);
                    // Media Errors bytes 160..175
                    ulong mediaErr = ReadUInt64(dataPtr, 160);
                    // Thermal Throttle Events: NVMe Health Log并无统一事件计数，此处沿用旧字段但置空
                    double? throttleCnt = null;

                    // Temperature sensors (1..4 常见)
                    double? ts1 = null, ts2 = null, ts3 = null, ts4 = null;
                    ushort s1 = ReadUInt16(dataPtr, 200);
                    if (s1 != 0) ts1 = s1 > 273 ? (double)(s1 - 273) : (double)s1;
                    ushort s2 = ReadUInt16(dataPtr, 202);
                    if (s2 != 0) ts2 = s2 > 273 ? (double)(s2 - 273) : (double)s2;
                    ushort s3 = ReadUInt16(dataPtr, 204);
                    if (s3 != 0) ts3 = s3 > 273 ? (double)(s3 - 273) : (double)s3;
                    ushort s4 = ReadUInt16(dataPtr, 206);
                    if (s4 != 0) ts4 = s4 > 273 ? (double)(s4 - 273) : (double)s4;

                    return new SmartSummary
                    {
                        temperature_c = tempC,
                        nvme_percentage_used = pctUsed,
                        nvme_data_units_read = Math.Round(dataReadGB, 2),
                        nvme_data_units_written = Math.Round(dataWriteGB, 2),
                        nvme_controller_busy_time_min = (double)busyMin,
                        nvme_power_cycles = (double)powerCycles,
                        nvme_power_on_hours = (double)poh,
                        unsafe_shutdowns = (double)unsafeCnt,
                        nvme_media_errors = (double)mediaErr,
                        nvme_available_spare = (double)availSpare,
                        nvme_spare_threshold = (double)spareTh,
                        nvme_critical_warning = crit,
                        nvme_temp_sensor1_c = ts1,
                        nvme_temp_sensor2_c = ts2,
                        nvme_temp_sensor3_c = ts3,
                        nvme_temp_sensor4_c = ts4,
                        thermal_throttle_events = throttleCnt
                    };
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuf);
                    Marshal.FreeHGlobal(outBuf);
                }
            }
            catch { return null; }
        }

        // ATA Identify Device（0xEC）读取序列号/型号/固件
        public static (string serial, string model, string firmware)? TryReadAtaIdentifyStrings(int physicalDriveIndex)
        {
            try
            {
                using var h = OpenPhysicalDrive(physicalDriveIndex);
                int inSize = Marshal.SizeOf<SENDCMDINPARAMS>() - 1;
                int outSize = Marshal.SizeOf<SENDCMDOUTPARAMS>() + 512;
                var inBuf = Marshal.AllocHGlobal(inSize);
                var outBuf = Marshal.AllocHGlobal(outSize);
                try
                {
                    var inParams = new SENDCMDINPARAMS();
                    inParams.cBufferSize = 512;
                    inParams.irDriveRegs.bFeaturesReg = 0;
                    inParams.irDriveRegs.bSectorCountReg = 1;
                    inParams.irDriveRegs.bSectorNumberReg = 1;
                    inParams.irDriveRegs.bCylLowReg = 0;
                    inParams.irDriveRegs.bCylHighReg = 0;
                    inParams.irDriveRegs.bDriveHeadReg = 0xA0;
                    inParams.irDriveRegs.bCommandReg = 0xEC; // IDENTIFY DEVICE
                    Marshal.StructureToPtr(inParams, inBuf, false);

                    if (!DeviceIoControl(h, SMART_RCV_DRIVE_DATA, inBuf, (uint)inSize, outBuf, (uint)outSize, out _, IntPtr.Zero))
                        return null;

                    var dataPtr = (IntPtr)(outBuf.ToInt64() + Marshal.SizeOf<SENDCMDOUTPARAMS>());
                    byte[] idData = new byte[512];
                    Marshal.Copy(dataPtr, idData, 0, 512);

                    static string ReadAtaString(byte[] buf, int wordStart, int wordCount)
                    {
                        int offset = wordStart * 2;
                        int bytes = wordCount * 2;
                        byte[] tmp = new byte[bytes];
                        Buffer.BlockCopy(buf, offset, tmp, 0, bytes);
                        // 每个 word 内部字节高低位交换
                        for (int i = 0; i < bytes; i += 2)
                        {
                            (tmp[i], tmp[i + 1]) = (tmp[i + 1], tmp[i]);
                        }
                        string s = System.Text.Encoding.ASCII.GetString(tmp).Trim('\0', ' ');
                        return s;
                    }

                    string serial = ReadAtaString(idData, 10, 10);   // words 10-19
                    string model = ReadAtaString(idData, 27, 20);    // words 27-46
                    string firmware = ReadAtaString(idData, 23, 4);  // words 23-26
                    if (string.IsNullOrWhiteSpace(serial) && string.IsNullOrWhiteSpace(model)) return null;
                    return (serial, model, firmware);
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuf);
                    Marshal.FreeHGlobal(outBuf);
                }
            }
            catch { return null; }
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

        // ======= ATA/SATA SMART 所需 IOCTL 与结构 =======
        private const uint SMART_GET_VERSION = 0x00074080;
        private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
        private const byte SMART_CMD = 0xB0;
        private const byte SMART_READ_DATA = 0xD0;

        private const uint CAP_SMART_CMD = 0x00000004;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct GETVERSIONINPARAMS
        {
            public byte bVersion;
            public byte bRevision;
            public byte bReserved;
            public byte bIDEDeviceMap;
            public uint fCapabilities;
            public uint dwReserved1;
            public uint dwReserved2;
            public uint dwReserved3;
            public uint dwReserved4;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct IDEREGS
        {
            public byte bFeaturesReg;
            public byte bSectorCountReg;
            public byte bSectorNumberReg;
            public byte bCylLowReg;
            public byte bCylHighReg;
            public byte bDriveHeadReg;
            public byte bCommandReg;
            public byte bReserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct DRIVERSTATUS
        {
            public byte bDriverError;
            public byte bIDEStatus;
            public byte bReserved0;
            public byte bReserved1;
            public uint dwReserved0;
            public uint dwReserved1;
        }

        // 注意：结构末尾含有可变大小缓冲区 bBuffer[1]
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct SENDCMDINPARAMS
        {
            public uint cBufferSize;
            public IDEREGS irDriveRegs;
            public byte bDriveNumber;
            public byte bReserved1;
            public byte bReserved2;
            public byte bReserved3;
            public uint dwReserved0;
            public uint dwReserved1;
            public uint dwReserved2;
            public uint dwReserved3;
            public byte bBuffer; // 占位，实际按需扩展
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct SENDCMDOUTPARAMS
        {
            public uint cBufferSize;
            public DRIVERSTATUS DriverStatus;
            public byte bBuffer; // 占位，实际后续紧跟数据区
        }

        // ======= 以下为 NVMe 协议特定查询所需结构/常量 =======
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        private enum STORAGE_PROPERTY_ID
        {
            StorageDeviceProperty = 0,
            StorageAdapterProperty = 1,
            StorageDeviceIdProperty = 2,
            StorageDeviceUniqueIdProperty = 3,
            StorageDeviceWriteCacheProperty = 4,
            StorageMiniportProperty = 5,
            StorageAccessAlignmentProperty = 6,
            StorageDeviceSeekPenaltyProperty = 7,
            StorageDeviceTrimProperty = 8,
            StorageDeviceProtocolSpecificProperty = 50
        }
        private enum STORAGE_QUERY_TYPE { PropertyStandardQuery = 0, PropertyExistsQuery, PropertyMaskQuery, PropertyQueryMaxDefined }

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROPERTY_QUERY
        {
            public STORAGE_PROPERTY_ID PropertyId;
            public STORAGE_QUERY_TYPE QueryType;
            // 注意：真正的结构后可紧跟附加参数字节流。此处不内嵌，仅在缓冲区中紧随其后写入 STORAGE_PROTOCOL_SPECIFIC_DATA。
        }

        private enum STORAGE_PROTOCOL_TYPE { ProtocolTypeUnknown = 0, ProtocolTypeScsi, ProtocolTypeAta, ProtocolTypeNvme = 4 }
        private enum STORAGE_PROTOCOL_NVME_DATA_TYPE { NVMeDataTypeUnknown = 0, NVMeDataTypeIdentify, NVMeDataTypeLogPage }

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROTOCOL_SPECIFIC_DATA
        {
            public STORAGE_PROTOCOL_TYPE ProtocolType;
            public uint DataType; // STORAGE_PROTOCOL_NVME_DATA_TYPE
            public uint ProtocolDataRequestValue;
            public uint ProtocolDataRequestSubValue;
            public uint ProtocolDataOffset;
            public uint ProtocolDataLength;
            public uint FixedProtocolReturnData;
            public uint ProtocolDataRequestSubValue2;
            public uint ProtocolDataRequestSubValue3;
            public uint ProtocolDataRequestSubValue4;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROTOCOL_DATA_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public STORAGE_PROTOCOL_SPECIFIC_DATA ProtocolSpecific;
        }

        private static ushort ReadUInt16(IntPtr basePtr, int offset)
        {
            return (ushort)(Marshal.ReadByte(basePtr, offset) | (Marshal.ReadByte(basePtr, offset + 1) << 8));
        }
        private static ulong ReadUInt64(IntPtr basePtr, int offset)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= ((ulong)Marshal.ReadByte(basePtr, offset + i)) << (8 * i);
            return v;
        }
    }
}
