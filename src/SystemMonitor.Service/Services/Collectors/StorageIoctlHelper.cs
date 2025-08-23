using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Linq;

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

        // 选择最合适的 NVMe 命名空间：
        // 1) 若 NN<=1，返回 1
        // 2) 遍历 1..NN，读取 Identify Namespace Basic
        //    - 首选 nuse 最大者（正在使用量最大）
        //    - 次选 nsze 最大者（容量最大）
        // 3) 若全部失败，返回 1
        public static uint TrySelectBestNvmeNamespaceId(int physicalDriveIndex)
        {
            try
            {
                var nn = TryReadNvmeIdentifyControllerNN(physicalDriveIndex) ?? 0u;
                if (nn <= 1) return 1u;

                uint bestId = 1u;
                ulong bestNuse = 0;
                ulong bestNsze = 0;

                for (uint nsid = 1; nsid <= nn && nsid <= 1024; nsid++)
                {
                    var basic = TryReadNvmeIdentifyNamespaceBasic(physicalDriveIndex, nsid);
                    if (basic.HasValue)
                    {
                        var (nsze, nuse, _, _, _) = basic.Value;
                        bool better = false;
                        if (nuse > bestNuse) better = true;
                        else if (nuse == bestNuse && nsze > bestNsze) better = true;
                        if (better)
                        {
                            bestId = nsid; bestNuse = nuse; bestNsze = nsze;
                        }
                    }
                }

                if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                {
                    try { Serilog.Log.Information("[SMART] NVMe namespace select idx={Idx} NN={NN} bestNSID={NSID} bestNuse={NUSE} bestNsze={NSZE}", physicalDriveIndex, nn, bestId, bestNuse, bestNsze); } catch { }
                }
                return bestId;
            }
            catch
            {
                return 1u;
            }
        }

        // 读取 NVMe Identify Namespace（基本信息：NSZE/NUSE、LBA Size、EUI64/NGUID）
        public static (ulong nsze_lba, ulong nuse_lba, int lba_size_bytes, string? eui64, string? nguid)? TryReadNvmeIdentifyNamespaceBasic(int physicalDriveIndex, uint namespaceId = 1)
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
                    ProtocolDataRequestValue = 0x00, // CNS = 0x00 -> Identify Namespace
                    ProtocolDataRequestSubValue = namespaceId, // NSID
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

                    if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, (uint)inSize, outBuf, (uint)outSize, out _, IntPtr.Zero))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                        {
                            try { Serilog.Log.Information("[SMART] NVMe Identify NS DeviceIoControl fail idx={Idx} nsid={NSID} err={Err}", physicalDriveIndex, namespaceId, err); } catch { }
                        }
                        // 87/50/1 触发回退到 IOCTL_STORAGE_PROTOCOL_COMMAND
                        if (err == 87 || err == 50 || err == 1)
                        {
                            // Identify Namespace (CNS=0x00)。尝试 nsid: 请求的、0、1。
                            foreach (var tryNs in new uint[] { namespaceId, 0u, 1u })
                            {
                                if (SendNvmeAdminCommand(h, NVME_ADMIN_OPC_IDENTIFY, tryNs,
                                    cdw10: 0x00u, cdw11: 0, cdw12: 0, cdw13: 0, cdw14: 0, cdw15: 0,
                                    dataInLen: 4096, dataOutLen: 0, out _, out var idNs))
                                {
                                    // 直接从回退缓冲解析并返回
                                    if (idNs != null && idNs.Length >= 512)
                                    {
                                    // 创建临时非托管块以复用现有解析逻辑
                                    IntPtr tmp = Marshal.AllocHGlobal(idNs.Length);
                                    try
                                    {
                                        Marshal.Copy(idNs, 0, tmp, idNs.Length);
                                        ulong nszeF = ReadUInt64(tmp, 0x00);
                                        ulong nuseF = ReadUInt64(tmp, 0x10);
                                        byte flbasF = Marshal.ReadByte(tmp, 0x1A);
                                        int fmtF = flbasF & 0x0F;
                                        int lbafBaseF = 0x80;
                                        byte lbadsF = Marshal.ReadByte(tmp, lbafBaseF + fmtF * 4 + 1);
                                        int lbaBytesF = 1 << lbadsF;
                                        static string ReadHex2(IntPtr basePtr, int offset, int len)
                                        {
                                            var buf = new byte[len];
                                            Marshal.Copy((IntPtr)(basePtr.ToInt64() + offset), buf, 0, len);
                                            return string.Concat(buf.Select(b => b.ToString("X2")));
                                        }
                                        string nguidF = ReadHex2(tmp, 104, 16); if (nguidF.All(c => c == '0')) nguidF = string.Empty;
                                        string euiF = ReadHex2(tmp, 120, 8); if (euiF.All(c => c == '0')) euiF = string.Empty;
                                        return (nszeF, nuseF, lbaBytesF, string.IsNullOrEmpty(euiF) ? null : euiF, string.IsNullOrEmpty(nguidF) ? null : nguidF);
                                    }
                                    finally { Marshal.FreeHGlobal(tmp); }
                                    }
                                    // 数据不足继续尝试
                                }
                                // IOCTL 失败或数据不足，尝试下一个 ns
                            }
                            return null;
                        }
                        else
                        {
                            return null;
                        }
                    }

                    var desc = Marshal.PtrToStructure<STORAGE_PROTOCOL_DATA_DESCRIPTOR>(outBuf);
                    var sp = desc.ProtocolSpecific;
                    if (sp.ProtocolDataLength < 512 || sp.ProtocolDataOffset == 0)
                    {
                        if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                        {
                            try { Serilog.Log.Information("[SMART] NVMe Identify NS invalid desc idx={Idx} len={Len} off={Off}", physicalDriveIndex, sp.ProtocolDataLength, sp.ProtocolDataOffset); } catch { }
                        }
                        return null;
                    }

                    var dataPtr = (IntPtr)(outBuf.ToInt64() + (int)sp.ProtocolDataOffset);

                    // Fields per NVMe Identify Namespace structure
                    ulong nsze = ReadUInt64(dataPtr, 0x00);
                    // ulong ncap = ReadUInt64(dataPtr, 0x08); // 可按需使用
                    ulong nuse = ReadUInt64(dataPtr, 0x10);
                    byte flbas = Marshal.ReadByte(dataPtr, 0x1A);
                    int fmt = flbas & 0x0F; // 当前格式索引
                    int lbafBase = 0x80; // 128
                    byte lbads = Marshal.ReadByte(dataPtr, lbafBase + fmt * 4 + 1);
                    int lbaBytes = 1 << lbads; // 2^LBADS

                    // EUI64 (120..127), NGUID (104..119)
                    static string ReadHex(IntPtr basePtr, int offset, int len)
                    {
                        byte[] buf = new byte[len];
                        Marshal.Copy((IntPtr)(basePtr.ToInt64() + offset), buf, 0, len);
                        return string.Concat(buf.Select(b => b.ToString("X2")));
                    }
                    string nguid = ReadHex(dataPtr, 104, 16);
                    if (nguid.All(c => c == '0')) nguid = string.Empty;
                    string eui = ReadHex(dataPtr, 120, 8);
                    if (eui.All(c => c == '0')) eui = string.Empty;

                    return (nsze, nuse, lbaBytes, string.IsNullOrEmpty(eui) ? null : eui, string.IsNullOrEmpty(nguid) ? null : nguid);
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuf);
                    Marshal.FreeHGlobal(outBuf);
                }
            }
            catch (Exception ex)
            {
                if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                {
                    try { Serilog.Log.Information("[SMART] NVMe Identify NS exception idx={Idx} msg={Msg}", physicalDriveIndex, ex.Message); } catch { }
                }
                return null;
            }
        }

        // 读取 NVMe Identify Controller 中的命名空间数量 NN（用于展示）
        public static uint? TryReadNvmeIdentifyControllerNN(int physicalDriveIndex)
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
                    ProtocolDataRequestValue = 0x01, // CNS = 0x01 -> Identify Controller
                    ProtocolDataRequestSubValue = 0,
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
                    if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, (uint)inSize, outBuf, (uint)outSize, out _, IntPtr.Zero))
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                        {
                            try { Serilog.Log.Information("[SMART] NVMe Identify Ctrl DeviceIoControl fail idx={Idx} err={Err}", physicalDriveIndex, err); } catch { }
                        }
                        if (err == 87 || err == 50 || err == 1)
                        {
                            // 回退：NVMe Identify Controller（CNS=0x01），读取 4096 字节
                            if (SendNvmeAdminCommand(h, NVME_ADMIN_OPC_IDENTIFY, 0,
                                cdw10: 0x01u, cdw11: 0, cdw12: 0, cdw13: 0, cdw14: 0, cdw15: 0,
                                dataInLen: 4096, dataOutLen: 0, out _, out var idCtlr))
                            {
                                if (idCtlr != null && idCtlr.Length >= 520)
                                {
                                    IntPtr tmp = Marshal.AllocHGlobal(idCtlr.Length);
                                    try
                                    {
                                        Marshal.Copy(idCtlr, 0, tmp, idCtlr.Length);
                                        uint nnF = (uint)(Marshal.ReadByte(tmp, 516)
                                                   | (Marshal.ReadByte(tmp, 517) << 8)
                                                   | (Marshal.ReadByte(tmp, 518) << 16)
                                                   | (Marshal.ReadByte(tmp, 519) << 24));
                                        return nnF;
                                    }
                                    finally { Marshal.FreeHGlobal(tmp); }
                                }
                                return null;
                            }
                            return null;
                        }
                        else
                        {
                            return null;
                        }
                    }
                    var desc = Marshal.PtrToStructure<STORAGE_PROTOCOL_DATA_DESCRIPTOR>(outBuf);
                    var sp = desc.ProtocolSpecific;
                    if (sp.ProtocolDataLength < 520 || sp.ProtocolDataOffset == 0)
                    {
                        if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                        {
                            try { Serilog.Log.Information("[SMART] NVMe Identify Ctrl invalid desc idx={Idx} len={Len} off={Off}", physicalDriveIndex, sp.ProtocolDataLength, sp.ProtocolDataOffset); } catch { }
                        }
                        return null;
                    }
                    var dataPtr = (IntPtr)(outBuf.ToInt64() + (int)sp.ProtocolDataOffset);
                    // NN at bytes 516..519 (LE)
                    uint nn = (uint)(Marshal.ReadByte(dataPtr, 516)
                               | (Marshal.ReadByte(dataPtr, 517) << 8)
                               | (Marshal.ReadByte(dataPtr, 518) << 16)
                               | (Marshal.ReadByte(dataPtr, 519) << 24));
                    return nn;
                }
                finally
                {
                    Marshal.FreeHGlobal(inBuf);
                    Marshal.FreeHGlobal(outBuf);
                }
            }
            catch { return null; }
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

        // SATA/ATA SMART（实现：读取 Attributes 与 Thresholds，计算整体健康）
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

                    // 读取 SMART 阈值（使用 READ_THRESHOLDS 命令）
                    var inBufTh = Marshal.AllocHGlobal(inSize);
                    var outBufTh = Marshal.AllocHGlobal(outSize);
                    try
                    {
                        var inParamsTh = new SENDCMDINPARAMS();
                        inParamsTh.cBufferSize = 512;
                        inParamsTh.irDriveRegs.bFeaturesReg = SMART_READ_THRESHOLDS; // 0xD1
                        inParamsTh.irDriveRegs.bSectorCountReg = 1;
                        inParamsTh.irDriveRegs.bSectorNumberReg = 1;
                        inParamsTh.irDriveRegs.bCylLowReg = 0x4F;
                        inParamsTh.irDriveRegs.bCylHighReg = 0xC2;
                        inParamsTh.irDriveRegs.bDriveHeadReg = 0xA0;
                        inParamsTh.irDriveRegs.bCommandReg = SMART_CMD;
                        Marshal.StructureToPtr(inParamsTh, inBufTh, false);

                        byte[] thrData = new byte[512];
                        if (DeviceIoControl(h, SMART_RCV_DRIVE_DATA, inBufTh, (uint)inSize, outBufTh, (uint)outSize, out _, IntPtr.Zero))
                        {
                            var thPtr = (IntPtr)(outBufTh.ToInt64() + Marshal.SizeOf<SENDCMDOUTPARAMS>());
                            Marshal.Copy(thPtr, thrData, 0, 512);
                        }

                        // 解析属性与阈值：30 个条目，每条 12 字节，从偏移 2 开始
                        double? tempC = null, poh = null, realloc = null, pending = null, crc = null;
                        string? overall = null;
                        bool anyPrefailFail = false, anyAdvisoryFail = false;

                        for (int i = 2; i < 2 + 12 * 30; i += 12)
                        {
                            byte id = smartData[i];
                            if (id == 0) continue;

                            // 规范化 Value 在 i+3；原始值 6 字节位于 i+5..i+10（LE）
                            byte norm = smartData[i + 3];
                            ulong raw = 0;
                            for (int b = 0; b < 6; b++) raw |= ((ulong)smartData[i + 5 + b]) << (8 * b);

                            // flags 两字节（i+1..i+2, LE）最低位常表示 pre-fail/advisory（1: pre-fail, 0: advisory）
                            ushort flags = (ushort)(smartData[i + 1] | (smartData[i + 2] << 8));
                            bool isPrefail = (flags & 0x01) != 0;

                            // 阈值表同样布局（id 匹配；阈值位于条目偏移 +1）
                            int thBase = -1;
                            for (int j = 2; j < 2 + 12 * 30; j += 12)
                            {
                                if (thrData[j] == id) { thBase = j; break; }
                            }
                            byte thr = (byte)(thBase >= 0 ? thrData[thBase + 1] : 0);

                            // 计算整体健康：若规范化值 <= 阈值且阈值>0，则判定失败
                            if (thr > 0 && norm > 0 && norm <= thr)
                            {
                                if (isPrefail) anyPrefailFail = true; else anyAdvisoryFail = true;
                            }

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
                                case 0xC2: // Temperature（优先规范化值作为摄氏）
                                    if (norm > 0 && norm < 125) tempC = norm; else tempC = (double)raw;
                                    break;
                            }
                        }

                        if (anyPrefailFail) overall = "critical";
                        else if (anyAdvisoryFail) overall = "warning";
                        else overall = "good";

                        return new SmartSummary
                        {
                            overall_health = overall,
                            temperature_c = tempC,
                            power_on_hours = poh,
                            reallocated_sector_count = realloc,
                            pending_sector_count = pending,
                            udma_crc_error_count = crc
                        };
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(inBufTh);
                        Marshal.FreeHGlobal(outBufTh);
                    }
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
                        int err = Marshal.GetLastWin32Error();
                        if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                        {
                            try { Serilog.Log.Information("[SMART] NVMe Health DeviceIoControl fail idx={Idx} err={Err}", physicalDriveIndex, err); } catch { }
                        }
                        if (err == 87 || err == 50 || err == 1)
                        {
                            // 回退：Get Log Page (Health, LID=0x02), NUMD=127 (512B)。兼容不同驱动的 NSID 要求：尝试 FFFFFFFF、0、1。
                            uint cdw10 = (127u << 16) | 0x02u; // NUMD | LID
                            uint cdw12 = (1u << 15); // RAE=1，提高兼容性
                            // 升级句柄权限用于直通（若可能）
                            var path = @$"\\.\PHYSICALDRIVE{physicalDriveIndex}";
                            var hProto = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                            if (hProto.IsInvalid)
                            {
                                // 无法获取更高权限，则继续用原句柄尝试；同时记录调试信息
                                if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                                {
                                    try { Serilog.Log.Information("[SMART][Fallback] Protocol using existing handle (cannot elevate access) idx={Idx}", physicalDriveIndex); } catch { }
                                }
                                hProto = h; // 使用现有句柄
                            }
                            uint[] nsids = new uint[] { 0xFFFFFFFFu, 0u, 1u };
                            foreach (var tryNsid in nsids)
                            {
                                if (SendNvmeAdminCommand(hProto, NVME_ADMIN_OPC_GET_LOG_PAGE, tryNsid,
                                    cdw10: cdw10, cdw11: 0, cdw12: cdw12, cdw13: 0, cdw14: 0, cdw15: 0,
                                    dataInLen: 512, dataOutLen: 0, out _, out var health))
                                {
                                    if (health != null && health.Length >= 512)
                                    {
                                        IntPtr tmp = Marshal.AllocHGlobal(health.Length);
                                        try
                                        {
                                            Marshal.Copy(health, 0, tmp, health.Length);
                                            byte critF = Marshal.ReadByte(tmp, 0);
                                            byte availSpareF = Marshal.ReadByte(tmp, 3);
                                            byte spareThF = Marshal.ReadByte(tmp, 4);
                                            ushort tempRawF = ReadUInt16(tmp, 1);
                                            double? tempCF = tempRawF > 0 ? (tempRawF > 273 ? (double)(tempRawF - 273) : (double)tempRawF) : (double?)null;
                                            byte usedF = Marshal.ReadByte(tmp, 5);
                                            double? pctUsedF = usedF <= 100 ? (double)usedF : (double)(usedF & 0x7F);
                                            ulong durF = ReadUInt64(tmp, 32);
                                            ulong duwF = ReadUInt64(tmp, 48);
                                            const double BYTES_PER_DU_F = 512000.0;
                                            double dataReadGB_F = durF * BYTES_PER_DU_F / 1_000_000_000.0;
                                            double dataWriteGB_F = duwF * BYTES_PER_DU_F / 1_000_000_000.0;
                                            ulong busyMinF = ReadUInt64(tmp, 96);
                                            ulong powerCyclesF = ReadUInt64(tmp, 112);
                                            ulong pohF = ReadUInt64(tmp, 128);
                                            ulong unsafeCntF = ReadUInt64(tmp, 144);
                                            ulong mediaErrF = ReadUInt64(tmp, 160);
                                            // 传感温度（可选）
                                            ushort t1F = ReadUInt16(tmp, 200);
                                            ushort t2F = ReadUInt16(tmp, 202);
                                            double? t1cF = t1F > 0 ? (t1F > 273 ? (double)(t1F - 273) : (double)t1F) : (double?)null;
                                            double? t2cF = t2F > 0 ? (t2F > 273 ? (double)(t2F - 273) : (double)t2F) : (double?)null;
                                            return new SmartSummary
                                            {
                                                overall_health = null,
                                                temperature_c = tempCF,
                                                power_on_hours = null,
                                                reallocated_sector_count = null,
                                                pending_sector_count = null,
                                                udma_crc_error_count = null,
                                                nvme_percentage_used = pctUsedF,
                                                nvme_data_units_read = dataReadGB_F,
                                                nvme_data_units_written = dataWriteGB_F,
                                                nvme_controller_busy_time_min = busyMinF,
                                                nvme_power_cycles = powerCyclesF,
                                                nvme_power_on_hours = pohF,
                                                unsafe_shutdowns = unsafeCntF,
                                                nvme_media_errors = mediaErrF,
                                                nvme_available_spare = availSpareF,
                                                nvme_spare_threshold = spareThF,
                                                nvme_critical_warning = critF,
                                                nvme_temp_sensor1_c = t1cF,
                                                nvme_temp_sensor2_c = t2cF
                                            };
                                        }
                                        finally { Marshal.FreeHGlobal(tmp); }
                                    }
                                    // 数据不足则继续尝试下一个 NSID
                                }
                                // IOCTL 失败或无数据，继续尝试下一个 NSID
                            }
                            return null;
                        }
                        else
                        {
                            return null;
                        }
                    }

                    // 解析输出：STORAGE_PROTOCOL_DATA_DESCRIPTOR + 数据区
                    var desc = Marshal.PtrToStructure<STORAGE_PROTOCOL_DATA_DESCRIPTOR>(outBuf);
                    if (desc.Version != (uint)Marshal.SizeOf<STORAGE_PROTOCOL_DATA_DESCRIPTOR>() ||
                        desc.Size != (uint)Marshal.SizeOf<STORAGE_PROTOCOL_DATA_DESCRIPTOR>())
                    {
                        if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                        {
                            try { Serilog.Log.Information("[SMART] NVMe Health invalid header idx={Idx} ver={Ver} size={Size}", physicalDriveIndex, desc.Version, desc.Size); } catch { }
                        }
                        return null;
                    }

                    var sp = desc.ProtocolSpecific;
                    if (sp.ProtocolDataLength < 512 || sp.ProtocolDataOffset == 0)
                    {
                        if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                        {
                            try { Serilog.Log.Information("[SMART] NVMe Health invalid desc idx={Idx} len={Len} off={Off}", physicalDriveIndex, sp.ProtocolDataLength, sp.ProtocolDataOffset); } catch { }
                        }
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
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;

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
            // 分级尝试：读写 -> 只读 -> 0 权限（部分查询在 0 也可用）
            SafeFileHandle TryOpen(uint access) => CreateFileW(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            var h = TryOpen(GENERIC_READ | GENERIC_WRITE);
            if (h.IsInvalid)
            {
                int e1 = Marshal.GetLastWin32Error();
                var h2 = TryOpen(GENERIC_READ);
                if (h2.IsInvalid)
                {
                    int e2 = Marshal.GetLastWin32Error();
                    var h3 = TryOpen(0);
                    if (h3.IsInvalid)
                    {
                        int e3 = Marshal.GetLastWin32Error();
                        // 若开启调试，记录三次错误码
                        if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                        {
                            try { Serilog.Log.Information("[SMART] Open {Path} failed. GRW={E1} GR={E2} ZERO={E3}", path, e1, e2, e3); } catch { }
                        }
                        throw new Win32Exception(e3, $"Open {path} failed");
                    }
                    else
                    {
                        if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                        {
                            try { Serilog.Log.Information("[SMART] Open {Path} with ACCESS=0 (fallback). PrevErr GRW={E1} GR={E2}", path, e1, e2); } catch { }
                        }
                        return h3;
                    }
                }
                else
                {
                    if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                    {
                        try { Serilog.Log.Information("[SMART] Open {Path} with GENERIC_READ (fallback). PrevErr GRW={E1}", path, e1); } catch { }
                    }
                    return h2;
                }
            }
            else
            {
                if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                {
                    try { Serilog.Log.Information("[SMART] Open {Path} with GENERIC_READ|GENERIC_WRITE", path); } catch { }
                }
                return h;
            }
        }

        // ======= ATA/SATA SMART 所需 IOCTL 与结构 =======
        private const uint SMART_GET_VERSION = 0x00074080;
        private const uint SMART_RCV_DRIVE_DATA = 0x0007C088;
        private const byte SMART_CMD = 0xB0;
        private const byte SMART_READ_DATA = 0xD0;
        private const byte SMART_READ_THRESHOLDS = 0xD1;

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
        // 依据头文件：#define IOCTL_STORAGE_PROTOCOL_COMMAND CTL_CODE(IOCTL_STORAGE_BASE, 0x04F0, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
        // 计算得出数值常量：(0x2D<<16) | (3<<14) | (0x4F0<<2) | 0 = 0x002DD3C0
        private const uint IOCTL_STORAGE_PROTOCOL_COMMAND = 0x002DD3C0;

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

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
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

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct STORAGE_PROTOCOL_DATA_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public STORAGE_PROTOCOL_SPECIFIC_DATA ProtocolSpecific;
        }

        // 按照 winioctl.h 定义的直通结构：用于 IOCTL_STORAGE_PROTOCOL_COMMAND 回退路径（后续实现使用）
        // 注意：该结构后面紧跟可变长度的 Command / Error / Data 缓冲区，本结构仅表示头部固定部分
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct STORAGE_PROTOCOL_COMMAND
        {
            public uint Version;      // 建议设为 1（STORAGE_PROTOCOL_STRUCTURE_VERSION）
            public uint Length;       // sizeof(STORAGE_PROTOCOL_COMMAND)
            public STORAGE_PROTOCOL_TYPE ProtocolType; // NVMe = 4
            public uint Flags;        // 读/写/无数据等标志
            public uint ReturnStatus; // 由内核返回
            public uint ErrorCode;    // 可选错误码
            public uint CommandLength;
            public uint ErrorInfoLength;
            public uint DataToDeviceTransferLength;
            public uint DataFromDeviceTransferLength;
            public uint TimeOutValue; // 秒
            public uint ErrorInfoOffset;
            public uint DataToDeviceBufferOffset;
            public uint DataFromDeviceBufferOffset;
            public uint CommandSpecific; // 命令特定字段（NVMe 可用于 NSID 等）
            public uint Reserved0;
            public uint FixedProtocolReturnData;  // e.g., NVMe CQE DW0
            public uint Reserved1_0;              // Reserved1[0]
            public uint Reserved1_1;              // Reserved1[1]
            public uint Reserved1_2;              // Reserved1[2]
            // BYTE Command[ANYSIZE_ARRAY]; // 变长数据区，调用时手工拼装缓冲
        }

        // 与本机 SDK 对齐的结构版本与标志（来自 ntddstor.h）
        private const uint STORAGE_PROTOCOL_STRUCTURE_VERSION = 0x1;
        private const uint STORAGE_PROTOCOL_COMMAND_FLAG_ADAPTER_REQUEST = 0x80000000; // 目标为适配器而非设备
        private const uint STORAGE_PROTOCOL_COMMAND_FLAG_DATA_IN = 0x00000001;  // 从设备读数据
        private const uint STORAGE_PROTOCOL_COMMAND_FLAG_DATA_OUT = 0x00000002; // 向设备写数据
        private const uint STORAGE_PROTOCOL_COMMAND_LENGTH_NVME = 0x40; // NVMe commands are always 64 bytes.
        private const uint STORAGE_PROTOCOL_SPECIFIC_NVME_ADMIN_COMMAND = 0x01;

        // NVMe Admin Opcodes（规范常量）
        private const byte NVME_ADMIN_OPC_IDENTIFY = 0x06;
        private const byte NVME_ADMIN_OPC_GET_LOG_PAGE = 0x02;

        // 构造并发送 NVMe Admin 命令（回退路径）
        private static bool SendNvmeAdminCommand(SafeFileHandle hDevice,
            byte opcode,
            uint nsid,
            uint cdw10,
            uint cdw11,
            uint cdw12,
            uint cdw13,
            uint cdw14,
            uint cdw15,
            uint dataInLen,
            uint dataOutLen,
            out IntPtr outDataPtr,
            out byte[]? outData)
        {
            outDataPtr = IntPtr.Zero;
            outData = null;

            // 仅支持读取型（dataInLen）场景；dataOutLen 目前不使用
            uint cmdLen = STORAGE_PROTOCOL_COMMAND_LENGTH_NVME;
            uint headerSize = (uint)Marshal.SizeOf<STORAGE_PROTOCOL_COMMAND>();

            // 计算 offsets（需指针对齐）
            uint commandOffset = headerSize;
            // 对齐到指针大小
            uint Align(uint v) { var a = (uint)IntPtr.Size; return (v + (a - 1)) & ~(a - 1u); }
            uint afterCmd = Align(commandOffset + cmdLen);
            // 预留错误信息区（便于驱动返回扩展状态），16 字节足够承载常见 NVMe 状态
            const uint errorInfoLength = 16;
            uint errorInfoOffset = afterCmd;
            uint afterErr = Align(errorInfoOffset + errorInfoLength);
            uint dataFromOffset = dataInLen > 0 ? afterErr : 0;
            uint total = (dataInLen > 0 ? (dataFromOffset + dataInLen) : afterErr);
            uint inSize = afterCmd;   // 输入仅需要头+命令
            uint outSize = total;     // 输出需要头+命令+数据（DataFrom）

            IntPtr inOut = Marshal.AllocHGlobal((int)total);
            try
            {
                // 清零缓冲，避免驱动读取未定义字节（使用托管缓冲避免 unsafe 要求）
                var zeroBuf = new byte[total];
                Marshal.Copy(zeroBuf, 0, inOut, (int)total);
                // 填充头部：对于 NVMe 直通，一般不需要 ADAPTER_REQUEST，直接面向设备
                uint flags = 0;
                if (dataInLen > 0) flags |= STORAGE_PROTOCOL_COMMAND_FLAG_DATA_IN;
                if (dataOutLen > 0) flags |= STORAGE_PROTOCOL_COMMAND_FLAG_DATA_OUT;
                var spc = new STORAGE_PROTOCOL_COMMAND
                {
                    Version = STORAGE_PROTOCOL_STRUCTURE_VERSION,
                    Length = (uint)Marshal.SizeOf<STORAGE_PROTOCOL_COMMAND>(),
                    ProtocolType = STORAGE_PROTOCOL_TYPE.ProtocolTypeNvme,
                    // 仅设置数据方向标志
                    Flags = flags,
                    ReturnStatus = 0,
                    ErrorCode = 0,
                    CommandLength = cmdLen,
                    ErrorInfoLength = errorInfoLength,
                    DataToDeviceTransferLength = dataOutLen,
                    DataFromDeviceTransferLength = dataInLen,
                    TimeOutValue = 30,
                    ErrorInfoOffset = errorInfoOffset,
                    DataToDeviceBufferOffset = 0,
                    DataFromDeviceBufferOffset = dataFromOffset,
                    // 按 ntddstor.h，NVMe 使用 IOCTL_STORAGE_PROTOCOL_COMMAND 时 CommandSpecific 应为 0
                    CommandSpecific = 0,
                    Reserved0 = 0,
                    FixedProtocolReturnData = 0,
                    Reserved1_0 = 0,
                    Reserved1_1 = 0,
                    Reserved1_2 = 0
                };

                Marshal.StructureToPtr(spc, inOut, false);

                // 填充 64 字节 NVMe 命令（不包含 PRP，由内核根据 DataFrom/To 缓冲处理）
                byte[] cmd = new byte[cmdLen];
                cmd[0] = opcode; // DW0 OPC
                // DW1 NSID
                Array.Copy(BitConverter.GetBytes(nsid), 0, cmd, 4, 4);
                // DW10..DW15 从偏移 40 开始（10*4）
                void WriteDW(int dwIndex, uint value)
                {
                    int offset = dwIndex * 4;
                    Array.Copy(BitConverter.GetBytes(value), 0, cmd, offset, 4);
                }
                WriteDW(10, cdw10);
                WriteDW(11, cdw11);
                WriteDW(12, cdw12);
                WriteDW(13, cdw13);
                WriteDW(14, cdw14);
                WriteDW(15, cdw15);

                // 将命令写入
                Marshal.Copy(cmd, 0, (IntPtr)(inOut.ToInt64() + commandOffset), (int)cmdLen);

                if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Serilog.Log.Information(
                            "[SMART][Fallback] Prep IOCTL: opc=0x{OPC:X2} nsid=0x{NSID:X8} flags=0x{Flags:X8} cmdLen={CmdLen} hdr={Hdr} offCmd={OffCmd} offErr={OffErr} lenErr={LenErr} offIn={OffIn} lenIn={LenIn} inSize={InSz} outSize={OutSz} ioctl=0x{Ioctl:X8} cdw10=0x{CDW10:X8} cdw11=0x{CDW11:X8} cdw12=0x{CDW12:X8}",
                            opcode, nsid, flags, cmdLen, headerSize, commandOffset, errorInfoOffset, errorInfoLength, dataFromOffset, dataInLen, inSize, outSize, IOCTL_STORAGE_PROTOCOL_COMMAND,
                            cdw10, cdw11, cdw12);
                    }
                    catch { }
                }

                // 执行 IOCTL（使用同一缓冲作为 in/out）
                // 注意：部分驱动在 METHOD_BUFFERED 路径要求输入大小与输出大小一致，这里按 outSize 传入
                bool ok = DeviceIoControl(hDevice, IOCTL_STORAGE_PROTOCOL_COMMAND,
                    inOut, outSize, inOut, outSize, out _, IntPtr.Zero);
                if (!ok)
                {
                    if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var last = Marshal.GetLastWin32Error();
                            Serilog.Log.Information("[SMART][Fallback] NVMe cmd opc=0x{OPC:X2} nsid=0x{NSID:X8} fail err={Err} inSize={InSz} outSize={OutSz}", opcode, nsid, last, inSize, outSize);
                            // 失败后立即读取头部，部分驱动即使返回 false 也会写入 ReturnStatus/ErrorCode
                            var spcHead = Marshal.PtrToStructure<STORAGE_PROTOCOL_COMMAND>(inOut);
                            Serilog.Log.Information("[SMART][Fallback] Header after fail: ReturnStatus=0x{RS:X8} ErrorCode=0x{EC:X8}", spcHead.ReturnStatus, spcHead.ErrorCode);
                        }
                        catch { }
                    }

                    // 方案0：仅头+命令（不带数据区）的探测调用（部分驱动对长度敏感，需要先探测）
                    try
                    {
                        var spcProbe = spc;
                        spcProbe.DataFromDeviceTransferLength = 0;
                        spcProbe.DataFromDeviceBufferOffset = 0;
                        // 保留 ErrorInfo 区以接收返回状态
                        Marshal.StructureToPtr(spcProbe, inOut, false);
                        uint outSizeProbe = afterErr;
                        if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                        { try { Serilog.Log.Information("[SMART][Fallback] Probe header+cmd only opc=0x{OPC:X2} nsid=0x{NSID:X8} outSize={OutSz}", opcode, nsid, outSizeProbe); } catch { } }
                        bool probeOk = DeviceIoControl(hDevice, IOCTL_STORAGE_PROTOCOL_COMMAND, inOut, outSizeProbe, inOut, outSizeProbe, out _, IntPtr.Zero);
                        if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var last2 = Marshal.GetLastWin32Error();
                                var spcRet2 = Marshal.PtrToStructure<STORAGE_PROTOCOL_COMMAND>(inOut);
                                Serilog.Log.Information("[SMART][Fallback] Probe result ok={Ok} err={Err} ReturnStatus=0x{RS:X8} ErrorCode=0x{EC:X8}", probeOk, last2, spcRet2.ReturnStatus, spcRet2.ErrorCode);
                            }
                            catch { }
                        }
                        // 恢复原始头（带数据区）以继续后续重试
                        Marshal.StructureToPtr(spc, inOut, false);
                    }
                    catch { }

                    // 方案A：分离 in/out 缓冲再试一次（不带 ADAPTER_REQUEST）
                    try
                    {
                        IntPtr inBuf = Marshal.AllocHGlobal((int)inSize);
                        IntPtr outBuf = Marshal.AllocHGlobal((int)outSize);
                        try
                        {
                            // 清零
                            var zeroIn = new byte[inSize];
                            var zeroOut = new byte[outSize];
                            Marshal.Copy(zeroIn, 0, inBuf, (int)inSize);
                            Marshal.Copy(zeroOut, 0, outBuf, (int)outSize);

                            // 复制头与命令
                            Marshal.StructureToPtr(spc, inBuf, false);
                            Marshal.Copy(cmd, 0, (IntPtr)(inBuf.ToInt64() + commandOffset), (int)cmdLen);
                            Marshal.StructureToPtr(spc, outBuf, false);
                            Marshal.Copy(cmd, 0, (IntPtr)(outBuf.ToInt64() + commandOffset), (int)cmdLen);

                            if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                            {
                                try { Serilog.Log.Information("[SMART][Fallback] Retry separate buffers opc=0x{OPC:X2} nsid=0x{NSID:X8}", opcode, nsid); } catch { }
                            }
                            ok = DeviceIoControl(hDevice, IOCTL_STORAGE_PROTOCOL_COMMAND, inBuf, inSize, outBuf, outSize, out _, IntPtr.Zero);
                            if (ok)
                            {
                                // 拷回 out 头到 inOut 以复用后续解析
                                byte[] tmp = new byte[outSize];
                                Marshal.Copy(outBuf, tmp, 0, (int)outSize);
                                Marshal.Copy(tmp, 0, inOut, (int)outSize);
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(inBuf);
                            Marshal.FreeHGlobal(outBuf);
                        }
                    }
                    catch { }
                    if (!ok)
                    {
                        // 方案B：在分离缓冲的基础上叠加 ADAPTER_REQUEST 再试
                        try
                        {
                            IntPtr inBuf2 = Marshal.AllocHGlobal((int)inSize);
                            IntPtr outBuf2 = Marshal.AllocHGlobal((int)outSize);
                            try
                            {
                                var spcAR = spc; spcAR.Flags |= STORAGE_PROTOCOL_COMMAND_FLAG_ADAPTER_REQUEST;
                                var zeroIn = new byte[inSize]; var zeroOut = new byte[outSize];
                                Marshal.Copy(zeroIn, 0, inBuf2, (int)inSize);
                                Marshal.Copy(zeroOut, 0, outBuf2, (int)outSize);
                                Marshal.StructureToPtr(spcAR, inBuf2, false);
                                Marshal.Copy(cmd, 0, (IntPtr)(inBuf2.ToInt64() + commandOffset), (int)cmdLen);
                                Marshal.StructureToPtr(spcAR, outBuf2, false);
                                Marshal.Copy(cmd, 0, (IntPtr)(outBuf2.ToInt64() + commandOffset), (int)cmdLen);
                                if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                                { try { Serilog.Log.Information("[SMART][Fallback] Retry separate+ADAPTER_REQUEST opc=0x{OPC:X2} nsid=0x{NSID:X8}", opcode, nsid); } catch { } }
                                ok = DeviceIoControl(hDevice, IOCTL_STORAGE_PROTOCOL_COMMAND, inBuf2, inSize, outBuf2, outSize, out _, IntPtr.Zero);
                                if (ok)
                                {
                                    byte[] tmp2 = new byte[outSize];
                                    Marshal.Copy(outBuf2, tmp2, 0, (int)outSize);
                                    Marshal.Copy(tmp2, 0, inOut, (int)outSize);
                                }
                            }
                            finally { Marshal.FreeHGlobal(inBuf2); Marshal.FreeHGlobal(outBuf2); }
                        }
                        catch { }
                    }
                    
                    if (!ok)
                    {
                        // 方案C：原地缓冲，叠加 ADAPTER_REQUEST 再最后一次
                        try
                        {
                            var spcRetry = spc; spcRetry.Flags |= STORAGE_PROTOCOL_COMMAND_FLAG_ADAPTER_REQUEST;
                            Marshal.StructureToPtr(spcRetry, inOut, false);
                            if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                            { try { Serilog.Log.Information("[SMART][Fallback] Retry with ADAPTER_REQUEST opc=0x{OPC:X2} nsid=0x{NSID:X8}", opcode, nsid); } catch { } }
                            // 最后一次同缓冲 + 适配器标志也保持 inSize=outSize 以提高兼容性
                            ok = DeviceIoControl(hDevice, IOCTL_STORAGE_PROTOCOL_COMMAND, inOut, outSize, inOut, outSize, out _, IntPtr.Zero);
                            if (!ok)
                            {
                                if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                                {
                                    try
                                    {
                                        var last = Marshal.GetLastWin32Error();
                                        var spcRet = Marshal.PtrToStructure<STORAGE_PROTOCOL_COMMAND>(inOut);
                                        Serilog.Log.Information("[SMART][Fallback] Retry fail err={Err} ReturnStatus=0x{RS:X8} ErrorCode=0x{EC:X8}", last, spcRet.ReturnStatus, spcRet.ErrorCode);
                                    }
                                    catch { }
                                }
                                return false;
                            }
                        }
                        catch { return false; }
                    }
                }

                // 读取头部返回状态用于调试
                if (string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_SMART_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var spcRet = Marshal.PtrToStructure<STORAGE_PROTOCOL_COMMAND>(inOut);
                        Serilog.Log.Information("[SMART][Fallback] NVMe cmd opc=0x{OPC:X2} nsid=0x{NSID:X8} ReturnStatus=0x{RS:X8} ErrorCode=0x{EC:X8}", opcode, nsid, spcRet.ReturnStatus, spcRet.ErrorCode);
                    }
                    catch { }
                }

                // 输出数据区（若存在）
                if (dataInLen > 0)
                {
                    outData = new byte[dataInLen];
                    IntPtr dataPtr = (IntPtr)(inOut.ToInt64() + dataFromOffset);
                    Marshal.Copy(dataPtr, outData, 0, (int)dataInLen);
                    outDataPtr = dataPtr; // 仅用于调试，不在 finally 释放（归 inOut 管理）
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (inOut != IntPtr.Zero) Marshal.FreeHGlobal(inOut);
            }
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
