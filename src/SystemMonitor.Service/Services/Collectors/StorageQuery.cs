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
        }

        public (IReadOnlyList<VolumeInfo> volumes, (long? total, long? used, long? free) totals) ReadVolumes()
        {
            var list = new List<VolumeInfo>();
            long total = 0, used = 0, free = 0;
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
                        bitlocker_encryption = null
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
                using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Index, Model, SerialNumber, FirmwareRevision, Size, Partitions, InterfaceType, Capabilities, CapabilitiesDescriptions, MediaType FROM Win32_DiskDrive");
                foreach (ManagementObject mo in searcher.Get())
                {
                    string id = Convert.ToString(mo["DeviceID"], CultureInfo.InvariantCulture) ?? string.Empty; // e.g., \\.-\PHYSICALDRIVE0
                    string? model = Convert.ToString(mo["Model"], CultureInfo.InvariantCulture);
                    string? serial = Convert.ToString(mo["SerialNumber"], CultureInfo.InvariantCulture);
                    string? fw = Convert.ToString(mo["FirmwareRevision"], CultureInfo.InvariantCulture);
                    long? size = TryInt64(mo["Size"]);
                    int? parts = TryInt32(mo["Partitions"]);
                    string? ifType = Convert.ToString(mo["InterfaceType"], CultureInfo.InvariantCulture);
                    string? mediaType = Convert.ToString(mo["MediaType"], CultureInfo.InvariantCulture);

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

                    list.Add(new PhysicalDiskInfo
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
                        trim_supported = trim
                    });
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
                            trim_supported = null
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
                json = RunPwshJson("Get-CimInstance Win32_DiskDrive | Select-Object DeviceID,Model,SerialNumber,FirmwareRevision,Size,Partitions,InterfaceType,MediaType");
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
                if (!proc.WaitForExit(4000)) { try { proc.Kill(); } catch { } }
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
                    trim_supported = null
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
                    trim_supported = null
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
