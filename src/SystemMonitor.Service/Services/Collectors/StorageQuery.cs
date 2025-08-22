using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

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
    }
}
