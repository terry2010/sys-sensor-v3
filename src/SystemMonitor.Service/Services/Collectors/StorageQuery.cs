using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class StorageQuery
    {
        private static readonly Lazy<StorageQuery> _inst = new(() => new StorageQuery());
        public static StorageQuery Instance => _inst.Value;

        public sealed class VolumeInfo
        {
            public string id { get; set; } = string.Empty;           // e.g., C:
            public string mount_point { get; set; } = string.Empty;  // same as id for Windows
            public string? fs_type { get; set; }
            public long? size_total_bytes { get; set; }
            public long? size_used_bytes { get; set; }
            public long? size_free_bytes { get; set; }
            public bool? read_only { get; set; }
            public bool? is_removable { get; set; }
            public string? bitlocker_encryption { get; set; } // on/off/unknown/null
            public double? free_percent { get; set; }
        }

        // 从诸如 \\ \\ . \\ PHYSICALDRIVE0 或 \\ . \PHYSICALDRIVE12 的字符串中提取数字索引
        private static int? ExtractPhysicalDriveIndex(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;
            try
            {
                // 常见格式："\\\\.\\PHYSICALDRIVE0" 或 "\\\\?\\IDE#..."（后者无索引）
                var s = deviceId.ToUpperInvariant();
                const string key = "PHYSICALDRIVE";
                var p = s.LastIndexOf(key, StringComparison.Ordinal);
                if (p >= 0)
                {
                    var start = p + key.Length;
                    var i = start;
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                    if (i > start)
                    {
                        if (int.TryParse(s.Substring(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                            return idx;
                    }
                }
            }
            catch { }
            return null;
        }

        // 统计分页/休眠文件大小总和（单位：字节），失败时返回 null
        public long? ReadVmSwapfilesBytes()
        {
            long total = 0;
            bool any = false;
            try
            {
                // 优先 Win32_PageFile 的 FileSize（字节）
                try
                {
                    using var s1 = new ManagementObjectSearcher("SELECT FileName, FileSize FROM Win32_PageFile");
                    foreach (ManagementObject mo in s1.Get())
                    {
                        long? fs = TryInt64(mo["FileSize"]);
                        if (fs.HasValue)
                        {
                            total += fs.Value; any = true;
                        }
                    }
                }
                catch { }

                // 回退：Win32_PageFileUsage 的 CurrentUsage（MB）
                if (!any)
                {
                    try
                    {
                        using var s2 = new ManagementObjectSearcher("SELECT CurrentUsage FROM Win32_PageFileUsage");
                        foreach (ManagementObject mo in s2.Get())
                        {
                            var mb = TryInt64(mo["CurrentUsage"]);
                            if (mb.HasValue)
                            {
                                total += mb.Value * 1024L * 1024L; any = true;
                            }
                        }
                    }
                    catch { }
                }

                // hiberfil.sys（通常在系统盘根目录）
                try
                {
                    var sysDrive = Environment.GetEnvironmentVariable("SystemDrive"); // e.g., C:
                    if (!string.IsNullOrWhiteSpace(sysDrive))
                    {
                        var hiber = Path.Combine(sysDrive, "hiberfil.sys");
                        if (File.Exists(hiber))
                        {
                            var fi = new FileInfo(hiber);
                            total += fi.Length; any = true;
                        }
                    }
                }
                catch { }
            }
            catch { }

            return any ? total : (long?)null;
        }

        public sealed class PhysicalDiskInfo
        {
            public string id { get; set; } = string.Empty; // DeviceID or Index
            public string? model { get; set; }
            public string? serial { get; set; }
            public string? firmware { get; set; }
            public long? size_total_bytes { get; set; }
            public int? partitions { get; set; }
            public string? media_type { get; set; } // ssd/hdd/unknown
            public int? spindle_speed_rpm { get; set; }
            public string? interface_type { get; set; } // SATA/NVMe/USB/...
            public bool? trim_supported { get; set; }
            public string? bus_type { get; set; } // duplicate of interface_type for schema alignment
            public string? negotiated_link_speed { get; set; } // e.g., "PCIe Gen4 x4" / "6 Gbps" (not implemented -> null)
            public bool? is_removable { get; set; }
            public bool? eject_capable { get; set; }
        }

        public (IReadOnlyList<VolumeInfo> volumes, (long? total, long? used, long? free) totals) ReadVolumes()
        {
            var list = new List<VolumeInfo>();
            long total = 0, used = 0, free = 0;
            // 预读取 BitLocker 状态映射（盘符 -> on/off/unknown）
            var bitlocker = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // 尝试连接 BitLocker WMI 命名空间
                var scope = new ManagementScope(@"\\.\ROOT\CIMV2\Security\MicrosoftVolumeEncryption");
                scope.Connect();
                using var blSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT ProtectionStatus, DriveLetter, MountPoint FROM Win32_EncryptableVolume"));
                foreach (ManagementObject mo in blSearcher.Get())
                {
                    string? drive = Convert.ToString(mo["DriveLetter"], CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(drive)) drive = Convert.ToString(mo["MountPoint"], CultureInfo.InvariantCulture);
                    // 统一为如 "C:" 的形式
                    if (!string.IsNullOrWhiteSpace(drive))
                    {
                        var key = drive!;
                        try
                        {
                            while (!string.IsNullOrEmpty(key) && (key.EndsWith("\\") || key.EndsWith("/")))
                                key = key.Substring(0, key.Length - 1);
                        }
                        catch { }
                        int? ps = TryInt32(mo["ProtectionStatus"]); // 0 Unknown, 1 Off, 2 On
                        string val = ps == 2 ? "on" : ps == 1 ? "off" : "unknown";
                        bitlocker[key] = val;
                    }
                }
            }
            catch { }
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT DeviceID, FileSystem, Size, FreeSpace, DriveType, Access FROM Win32_LogicalDisk");
                foreach (ManagementObject mo in searcher.Get())
                {
                    string id = Convert.ToString(mo["DeviceID"], CultureInfo.InvariantCulture) ?? string.Empty; // C:
                    string mount = id + "\\";
                    long? size = TryInt64(mo["Size"]);
                    long? freeBytes = TryInt64(mo["FreeSpace"]);
                    long? usedBytes = (size.HasValue && freeBytes.HasValue) ? (size.Value - freeBytes.Value) : (long?)null;
                    var fs = Convert.ToString(mo["FileSystem"], CultureInfo.InvariantCulture);
                    int? driveType = TryInt32(mo["DriveType"]);
                    bool? isRemovable = driveType.HasValue ? driveType.Value == 2 /*Removable Disk*/ : (bool?)null;
                    // Access: 0 (Unknown), 1 (Readable), 2 (Writable), 3 (Read/Write). Some systems may not populate.
                    int? access = TryInt32(mo["Access"]);
                    bool? readOnly = access.HasValue ? access.Value == 1 : (bool?)null;
                    string? bl = null;
                    try
                    {
                        if (bitlocker.TryGetValue(id, out var s)) bl = s;
                    }
                    catch { }

                    var vi = new VolumeInfo
                    {
                        id = id,
                        mount_point = mount,
                        fs_type = fs,
                        size_total_bytes = size,
                        size_used_bytes = usedBytes,
                        size_free_bytes = freeBytes,
                        read_only = readOnly,
                        is_removable = isRemovable,
                        bitlocker_encryption = bl
                    };
                    if (size.HasValue && size.Value > 0 && freeBytes.HasValue)
                    {
                        vi.free_percent = Math.Clamp(freeBytes.Value * 100.0 / size.Value, 0.0, 100.0);
                    }
                    list.Add(vi);

                    if (size.HasValue) total += size.Value;
                    if (usedBytes.HasValue) used += usedBytes.Value;
                    if (freeBytes.HasValue) free += freeBytes.Value;
                }
            }
            catch
            {
                // swallow and return what we have
            }
            return (list, (list.Count > 0 ? total : null, list.Count > 0 ? used : null, list.Count > 0 ? free : null));
        }

        public IReadOnlyList<PhysicalDiskInfo> ReadPhysicalDisks()
        {
            var list = new List<PhysicalDiskInfo>();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Index, Model, SerialNumber, FirmwareRevision, Size, Partitions, InterfaceType, Capabilities, CapabilitiesDescriptions, MediaType, PNPDeviceID FROM Win32_DiskDrive");
                foreach (ManagementObject mo in searcher.Get())
                {
                    string id = Convert.ToString(mo["DeviceID"], CultureInfo.InvariantCulture) ?? string.Empty; // e.g., \\.\PHYSICALDRIVE0
                    string? model = Convert.ToString(mo["Model"], CultureInfo.InvariantCulture);
                    string? pnp = Convert.ToString(mo["PNPDeviceID"], CultureInfo.InvariantCulture);
                    string? serial = Convert.ToString(mo["SerialNumber"], CultureInfo.InvariantCulture);
                    string? fw = Convert.ToString(mo["FirmwareRevision"], CultureInfo.InvariantCulture);
                    long? size = TryInt64(mo["Size"]);
                    int? parts = TryInt32(mo["Partitions"]);
                    string? ifType = Convert.ToString(mo["InterfaceType"], CultureInfo.InvariantCulture);
                    string? mediaType = Convert.ToString(mo["MediaType"], CultureInfo.InvariantCulture);
                    string[]? capsDesc = null;
                    try { capsDesc = mo["CapabilitiesDescriptions"] as string[]; } catch { }

                    bool? trim = null;
                    try
                    {
                        // Heuristic: SSD likely supports TRIM; if InterfaceType is SATA and media contains "SSD"
                        var ift = (ifType ?? string.Empty).ToUpperInvariant();
                        var mt = (mediaType ?? string.Empty).ToUpperInvariant();
                        trim = (ift.Contains("SATA") || ift.Contains("NVME")) && (mt.Contains("SSD") || (model ?? string.Empty).ToUpperInvariant().Contains("SSD"));
                    }
                    catch { }

                    int? rpm = null;
                    try
                    {
                        // Some vendors encode RPM in model; we avoid complex parsing and leave null
                        rpm = null;
                    }
                    catch { }

                    string? normMedia = null;
                    if (!string.IsNullOrEmpty(mediaType))
                    {
                        var mt = mediaType.ToUpperInvariant();
                        if (mt.Contains("SSD")) normMedia = "ssd";
                        else if (mt.Contains("HDD") || mt.Contains("FIXED")) normMedia = "hdd";
                    }

                    string? iface = null;
                    if (!string.IsNullOrEmpty(ifType))
                    {
                        var s = ifType.ToUpperInvariant();
                        if (s.Contains("NVME")) iface = "nvme";
                        else if (s.Contains("SATA")) iface = "sata";
                        else if (s.Contains("USB")) iface = "usb";
                        else if (s.Contains("SCSI") || s.Contains("SAS")) iface = "scsi";
                    }
                    // 常见场景：UASP 设备在 InterfaceType 显示为 SCSI，但 PNPDeviceID 含 USBSTOR
                    try
                    {
                        var pnpUpper = (pnp ?? string.Empty).ToUpperInvariant();
                        var modelUpper = (model ?? string.Empty).ToUpperInvariant();
                        if (pnpUpper.Contains("USBSTOR") || pnpUpper.Contains("USB\\") || modelUpper.Contains("USB") || modelUpper.Contains("UASP"))
                        {
                            iface = "usb";
                        }
                    }
                    catch { }

                    bool? removable = null;
                    bool? eject = null;
                    try
                    {
                        if (capsDesc != null && capsDesc.Length > 0)
                        {
                            var anyRem = capsDesc.Any(x => !string.IsNullOrEmpty(x) && x!.IndexOf("Removable", StringComparison.OrdinalIgnoreCase) >= 0);
                            var anyEject = capsDesc.Any(x => !string.IsNullOrEmpty(x) && x!.IndexOf("Eject", StringComparison.OrdinalIgnoreCase) >= 0);
                            if (anyRem) removable = true;
                            if (anyEject) eject = true;
                        }
                    }
                    catch { }
                    // 若 WMI 未提供明确能力或读取过程中异常，但判定为 USB（或 PNP/Model 显示 USB/UASP），则按启发式默认可移除/可弹出
                    if (string.Equals(iface, "usb", StringComparison.OrdinalIgnoreCase) || ((pnp ?? string.Empty).ToUpperInvariant().Contains("USB") || (model ?? string.Empty).ToUpperInvariant().Contains("USB") || (model ?? string.Empty).ToUpperInvariant().Contains("UASP")))
                    {
                        if (!removable.HasValue) removable = true;
                        if (!eject.HasValue) eject = true;
                    }

                    string? link = null;
                    try
                    {
                        var idx = ExtractPhysicalDriveIndex(id);
                        if (idx.HasValue)
                        {
                            link = StorageIoctlHelper.TryGetNegotiatedLinkSpeed(idx.Value, iface, model, pnp);
                        }
                    }
                    catch { }

                    var item = new PhysicalDiskInfo
                    {
                        id = id,
                        model = model,
                        serial = serial,
                        firmware = fw,
                        size_total_bytes = size,
                        partitions = parts,
                        media_type = normMedia,
                        spindle_speed_rpm = rpm,
                        interface_type = iface,
                        trim_supported = trim,
                        bus_type = iface,
                        negotiated_link_speed = link,
                        is_removable = removable,
                        eject_capable = eject
                    };
                    try { LogDiag($"  WMI disk id={item.id}, model={item.model}, pnp={pnp}, iface={item.interface_type}, removable={item.is_removable}, eject={item.eject_capable}, link={item.negotiated_link_speed}"); } catch { }
                    list.Add(item);
                }
            }
            catch
            {
                // ignore
            }

            // 回退：若 Win32_DiskDrive 未得到任何结果，尝试 MSFT_PhysicalDisk（需 Windows 8+）
            if (list.Count == 0)
            {
                try
                {
                    var scope = new ManagementScope(@"\\.\\ROOT\\Microsoft\\Windows\\Storage");
                    scope.Connect();
                    var qs = new ObjectQuery("SELECT FriendlyName, SerialNumber, Size, MediaType, SpindleSpeed, BusType, FirmwareVersion, DeviceId FROM MSFT_PhysicalDisk");
                    using var s = new ManagementObjectSearcher(scope, qs);
                    foreach (ManagementObject mo in s.Get())
                    {
                        string? id = Convert.ToString(mo["DeviceId"], CultureInfo.InvariantCulture) ?? Convert.ToString(mo["FriendlyName"], CultureInfo.InvariantCulture);
                        string? model = Convert.ToString(mo["FriendlyName"], CultureInfo.InvariantCulture);
                        string? serial = Convert.ToString(mo["SerialNumber"], CultureInfo.InvariantCulture);
                        string? fw = Convert.ToString(mo["FirmwareVersion"], CultureInfo.InvariantCulture);
                        long? size = TryInt64(mo["Size"]);

                        string? media = null;
                        try
                        {
                            // 先尝试数字枚举：3-HDD, 4-SSD
                            var mtNum = TryInt32(mo["MediaType"]);
                            if (mtNum.HasValue)
                            {
                                if (mtNum.Value == 4) media = "ssd";
                                else if (mtNum.Value == 3) media = "hdd";
                            }
                            if (media is null)
                            {
                                var mt = Convert.ToString(mo["MediaType"], CultureInfo.InvariantCulture)?.ToUpperInvariant();
                                if (!string.IsNullOrEmpty(mt))
                                {
                                    if (mt!.Contains("SSD")) media = "ssd";
                                    else if (mt.Contains("HDD") || mt.Contains("HARD")) media = "hdd";
                                }
                            }
                        }
                        catch { }

                        int? rpm = null;
                        try { rpm = TryInt32(mo["SpindleSpeed"]); } catch { }

                        string? iface = null;
                        try
                        {
                            // 优先数字枚举：17-NVMe, 11-SATA, 10-SAS, 7-USB, 1-SCSI
                            var btNum = TryInt32(mo["BusType"]);
                            if (btNum.HasValue)
                            {
                                iface = btNum.Value switch
                                {
                                    17 => "nvme",
                                    11 => "sata",
                                    10 => "scsi",
                                    7  => "usb",
                                    1  => "scsi",
                                    _  => null
                                };
                            }
                            if (iface is null)
                            {
                                var bt = Convert.ToString(mo["BusType"], CultureInfo.InvariantCulture)?.ToUpperInvariant();
                                if (!string.IsNullOrEmpty(bt))
                                {
                                    if (bt!.Contains("NVME")) iface = "nvme";
                                    else if (bt.Contains("SATA")) iface = "sata";
                                    else if (bt.Contains("USB")) iface = "usb";
                                    else if (bt.Contains("SAS") || bt.Contains("SCSI")) iface = "scsi";
                                }
                            }
                        }
                        catch { }

                        string? link = null;
                        try
                        {
                            // MSFT_PhysicalDisk.DeviceId 通常是数字索引
                            if (int.TryParse(id ?? string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idxNum))
                            {
                                link = StorageIoctlHelper.TryGetNegotiatedLinkSpeed(idxNum, iface);
                            }
                        }
                        catch { }

                        bool? removable = null;
                        bool? eject = null;
                        if (string.Equals(iface, "usb", StringComparison.OrdinalIgnoreCase))
                        {
                            removable = true;
                            eject = true;
                        }

                        list.Add(new PhysicalDiskInfo
                        {
                            id = id ?? Guid.NewGuid().ToString("N"),
                            model = model,
                            serial = serial,
                            firmware = fw,
                            size_total_bytes = size,
                            partitions = null,
                            media_type = media,
                            spindle_speed_rpm = rpm,
                            interface_type = iface,
                            trim_supported = null,
                            bus_type = iface,
                            negotiated_link_speed = link,
                            is_removable = removable,
                            eject_capable = eject
                        });
                    }
                }
                catch { }
            }
            // 第三层回退：使用 PowerShell CIM（外部进程）
            if (list.Count == 0)
            {
                try
                {
                    var viaPwsh = ReadPhysicalDisksViaPwsh();
                    if (viaPwsh.Count > 0)
                    {
                        list.AddRange(viaPwsh);
                        LogDiag($"[StorageQuery] Fallback via PowerShell CIM, got {viaPwsh.Count} items");
                    }
                    else
                    {
                        LogDiag("[StorageQuery] PowerShell CIM fallback returned 0 items");
                    }
                }
                catch (Exception ex)
                {
                    LogDiag($"[StorageQuery] PowerShell CIM fallback error: {ex.Message}");
                }
            }
            try
            {
                LogDiag($"[StorageQuery] PhysicalDisks count={list.Count}");
                if (list.Count > 0)
                {
                    foreach (var it in list.Take(3))
                    {
                        LogDiag($"  id={it.id}, model={it.model}, size={it.size_total_bytes}, iface={it.interface_type}, media={it.media_type}");
                    }
                }
            }
            catch { }
            return list;
        }

        private static long? TryInt64(object? o)
        {
            try { return o is null ? (long?)null : Convert.ToInt64(o, CultureInfo.InvariantCulture); }
            catch { return null; }
        }
        private static int? TryInt32(object? o)
        {
            try { return o is null ? (int?)null : Convert.ToInt32(o, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static List<PhysicalDiskInfo> ReadPhysicalDisksViaPwsh()
        {
            // 优先 MSFT_PhysicalDisk，再回退 Win32_DiskDrive
            var list = new List<PhysicalDiskInfo>();
            string? json = null;
            json = RunPwshJson("Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_PhysicalDisk | Select-Object DeviceId,FriendlyName,SerialNumber,Size,MediaType,SpindleSpeed,BusType,FirmwareVersion");
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    list.AddRange(ParseMsftPhysicalDiskJson(json));
                }
                catch { }
            }
            if (list.Count == 0)
            {
                json = RunPwshJson("Get-CimInstance Win32_DiskDrive | Select-Object DeviceID,Model,SerialNumber,FirmwareRevision,Size,Partitions,InterfaceType,MediaType,CapabilitiesDescriptions,PNPDeviceID");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    try { list.AddRange(ParseWin32DiskDriveJson(json)); } catch { }
                }
            }
            return list;
        }

        private static string? RunPwshJson(string command)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"$ErrorActionPreference='SilentlyContinue'; {command} | ConvertTo-Json -Depth 6\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var proc = Process.Start(psi);
                if (proc == null) return null;
                
                var sb = new StringBuilder();
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                proc.BeginOutputReadLine();
                var err = proc.StandardError.ReadToEnd();
                
                // 使用超时等待进程结束，避免无限期阻塞
                if (!proc.WaitForExit(4000)) 
                { 
                    try 
                    { 
                        proc.Kill(); 
                        proc.WaitForExit(1000); // 等待进程完全终止
                    } 
                    catch 
                    { 
                        // 忽略终止进程时的异常
                    } 
                }
                
                var output = sb.Length > 0 ? sb.ToString() : null;
                if (!string.IsNullOrWhiteSpace(err)) LogDiag($"[StorageQuery] pwsh err: {err.Trim()}\n");
                return output;
            }
            catch (Exception ex)
            {
                LogDiag($"[StorageQuery] RunPwshJson error: {ex.Message}");
                return null;
            }
        }

        private static IEnumerable<PhysicalDiskInfo> ParseMsftPhysicalDiskJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object) return ParseMsftPhysicalDiskJsonArray(new[] { root });
            if (root.ValueKind == JsonValueKind.Array) return ParseMsftPhysicalDiskJsonArray(root.EnumerateArray());
            return Array.Empty<PhysicalDiskInfo>();
        }

        private static IEnumerable<PhysicalDiskInfo> ParseMsftPhysicalDiskJsonArray(IEnumerable<JsonElement> arr)
        {
            var list = new List<PhysicalDiskInfo>();
            foreach (var el in arr)
            {
                string? id = el.TryGetProperty("DeviceId", out var pId) ? pId.ToString() : null;
                string? model = el.TryGetProperty("FriendlyName", out var pName) ? pName.ToString() : null;
                string? serial = el.TryGetProperty("SerialNumber", out var pSn) ? pSn.ToString() : null;
                string? fw = el.TryGetProperty("FirmwareVersion", out var pFw) ? pFw.ToString() : null;
                long? size = el.TryGetProperty("Size", out var pSize) && pSize.TryGetInt64(out var lv) ? lv : (long?)null;
                int? mediaNum = el.TryGetProperty("MediaType", out var pMt) && pMt.TryGetInt32(out var mv) ? mv : (int?)null;
                int? rpm = el.TryGetProperty("SpindleSpeed", out var pRpm) && pRpm.TryGetInt32(out var rv) ? rv : (int?)null;
                int? busNum = el.TryGetProperty("BusType", out var pBus) && pBus.TryGetInt32(out var bv) ? bv : (int?)null;

                string? media = mediaNum == 4 ? "ssd" : mediaNum == 3 ? "hdd" : null;
                string? iface = busNum switch { 17 => "nvme", 11 => "sata", 10 => "scsi", 7 => "usb", 1 => "scsi", _ => null };

                string? link = null;
                try
                {
                    if (int.TryParse(id ?? string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idxNum))
                    {
                        link = StorageIoctlHelper.TryGetNegotiatedLinkSpeed(idxNum, iface, model, null);
                    }
                }
                catch { }

                bool? removable = null;
                bool? eject = null;
                if (string.Equals(iface, "usb", StringComparison.OrdinalIgnoreCase))
                {
                    removable = true;
                    eject = true;
                }

                list.Add(new PhysicalDiskInfo
                {
                    id = id ?? (model ?? "disk"),
                    model = model,
                    serial = serial,
                    firmware = fw,
                    size_total_bytes = size,
                    partitions = null,
                    media_type = media,
                    spindle_speed_rpm = rpm,
                    interface_type = iface,
                    trim_supported = null,
                    bus_type = iface,
                    negotiated_link_speed = link,
                    is_removable = removable,
                    eject_capable = eject
                });
            }
            return list;
        }

        private static IEnumerable<PhysicalDiskInfo> ParseWin32DiskDriveJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object) return ParseWin32DiskDriveJsonArray(new[] { root });
            if (root.ValueKind == JsonValueKind.Array) return ParseWin32DiskDriveJsonArray(root.EnumerateArray());
            return Array.Empty<PhysicalDiskInfo>();
        }

        private static IEnumerable<PhysicalDiskInfo> ParseWin32DiskDriveJsonArray(IEnumerable<JsonElement> arr)
        {
            var list = new List<PhysicalDiskInfo>();
            foreach (var el in arr)
            {
                string id = el.TryGetProperty("DeviceID", out var pId) ? pId.ToString() : string.Empty;
                string? model = el.TryGetProperty("Model", out var pName) ? pName.ToString() : null;
                string? serial = el.TryGetProperty("SerialNumber", out var pSn) ? pSn.ToString() : null;
                string? fw = el.TryGetProperty("FirmwareRevision", out var pFw) ? pFw.ToString() : null;
                long? size = el.TryGetProperty("Size", out var pSize) && pSize.TryGetInt64(out var lv) ? lv : (long?)null;
                int? parts = el.TryGetProperty("Partitions", out var pParts) && pParts.TryGetInt32(out var pv) ? pv : (int?)null;
                string? ifType = el.TryGetProperty("InterfaceType", out var pIf) ? pIf.ToString() : null;
                string? mediaRaw = el.TryGetProperty("MediaType", out var pMt) ? pMt.ToString() : null;
                string? pnp = el.TryGetProperty("PNPDeviceID", out var pPnp) ? pPnp.ToString() : null;
                JsonElement capsEl;

                string? iface = null;
                if (!string.IsNullOrEmpty(ifType))
                {
                    var s = ifType.ToUpperInvariant();
                    if (s.Contains("NVME")) iface = "nvme";
                    else if (s.Contains("SATA")) iface = "sata";
                    else if (s.Contains("USB")) iface = "usb";
                    else if (s.Contains("SCSI") || s.Contains("SAS")) iface = "scsi";
                }
                string? media = null;
                if (!string.IsNullOrEmpty(mediaRaw))
                {
                    var mt = mediaRaw.ToUpperInvariant();
                    if (mt.Contains("SSD")) media = "ssd";
                    else if (mt.Contains("HDD") || mt.Contains("FIXED")) media = "hdd";
                }

                bool? removable = null;
                bool? eject = null;
                try
                {
                    if (el.TryGetProperty("CapabilitiesDescriptions", out capsEl))
                    {
                        if (capsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var ce in capsEl.EnumerateArray())
                            {
                                if (ce.ValueKind == JsonValueKind.String)
                                {
                                    var s = ce.GetString();
                                    if (!string.IsNullOrEmpty(s))
                                    {
                                        if (s.IndexOf("Removable", StringComparison.OrdinalIgnoreCase) >= 0) removable = true;
                                        if (s.IndexOf("Eject", StringComparison.OrdinalIgnoreCase) >= 0) eject = true;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                // 若判定为 USB 或 PNP 显示 USBSTOR，则默认可移除/可弹出
                try
                {
                    var pnpUpper = (pnp ?? string.Empty).ToUpperInvariant();
                    if (string.Equals(iface, "usb", StringComparison.OrdinalIgnoreCase) || pnpUpper.Contains("USBSTOR"))
                    {
                        if (!removable.HasValue) removable = true;
                        if (!eject.HasValue) eject = true;
                    }
                }
                catch { }

                string? link = null;
                try
                {
                    var idx = ExtractPhysicalDriveIndex(id);
                    if (idx.HasValue)
                    {
                        link = StorageIoctlHelper.TryGetNegotiatedLinkSpeed(idx.Value, iface, model, pnp);
                    }
                }
                catch { }

                list.Add(new PhysicalDiskInfo
                {
                    id = id,
                    model = model,
                    serial = serial,
                    firmware = fw,
                    size_total_bytes = size,
                    partitions = parts,
                    media_type = media,
                    spindle_speed_rpm = null,
                    interface_type = iface,
                    trim_supported = null,
                    bus_type = iface,
                    negotiated_link_speed = link,
                    is_removable = removable,
                    eject_capable = eject
                });
            }
            return list;
        }

        private static void LogDiag(string message)
        {
            try
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";

                // 1) 写 EXE 目录 logs
                try
                {
                    var baseDir = AppContext.BaseDirectory;
                    var logDir = Path.Combine(baseDir, "logs");
                    Directory.CreateDirectory(logDir);
                    var logFile = Path.Combine(logDir, $"service-{DateTime.Now:yyyyMMdd}.log");
                    File.AppendAllText(logFile, line);
                }
                catch { }

                // 2) 写当前工作目录 logs（通常是仓库根目录）
                try
                {
                    var cwd = Directory.GetCurrentDirectory();
                    var logDir2 = Path.Combine(cwd, "logs");
                    Directory.CreateDirectory(logDir2);
                    var logFile2 = Path.Combine(logDir2, $"service-{DateTime.Now:yyyyMMdd}.log");
                    File.AppendAllText(logFile2, line);
                }
                catch { }
            }
            catch
            {
                // ignore any logging failure
            }
        }
    }
}
