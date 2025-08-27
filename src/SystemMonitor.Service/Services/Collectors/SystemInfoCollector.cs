using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SystemMonitor.Service.Services.Collectors
{
    /// <summary>
    /// 系统信息采集器
    /// 采集机器硬件信息、操作系统版本、网络标识等静态或缓慢变化的系统信息
    /// 数据缓存5分钟，避免频繁WMI查询影响性能
    /// </summary>
    internal sealed class SystemInfoCollector : IMetricsCollector
    {
        public string Name => "system_info";

        private static readonly object _lock = new();
        private static long _cacheTs = 0;
        private static object? _cachedData = null;
        private const int CacheTtlMs = 300_000; // 5分钟缓存

        public object? Collect()
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                lock (_lock)
                {
                    if (_cachedData != null && (now - _cacheTs) < CacheTtlMs)
                    {
                        return _cachedData;
                    }
                }

                var data = CollectSystemInfo();
                
                lock (_lock)
                {
                    _cachedData = data;
                    _cacheTs = now;
                }

                return data;
            }
            catch (Exception ex)
            {
                // 故障隔离：返回基础信息，不影响其他采集器
                return CreateFallbackData(ex.Message);
            }
        }

        /// <summary>
        /// 采集完整系统信息
        /// </summary>
        private static object CollectSystemInfo()
        {
            // 机器与操作系统信息
            var machineInfo = GetMachineInfo();
            var osInfo = GetOperatingSystemInfo();
            var cpuInfo = GetCpuInfo();
            var memoryInfo = GetMemoryInfo();
            var graphicsInfo = GetGraphicsInfo();
            var biosInfo = GetBiosInfo();
            
            // 网络与标识信息
            var networkInfo = GetNetworkIdentification();

            return new
            {
                // 机器硬件信息
                machine = new
                {
                    manufacturer = machineInfo.manufacturer,
                    model = machineInfo.model,
                    serial_number = machineInfo.serialNumber,
                    uuid = machineInfo.uuid,
                    chassis_type = machineInfo.chassisType
                },
                
                // 操作系统信息
                operating_system = new
                {
                    name = osInfo.name,
                    version = osInfo.version,
                    build_number = osInfo.buildNumber,
                    architecture = osInfo.architecture,
                    install_date = osInfo.installDate,
                    last_boot_time = osInfo.lastBootTime,
                    uptime_hours = osInfo.uptimeHours
                },
                
                // CPU信息
                processor = new
                {
                    name = cpuInfo.name,
                    manufacturer = cpuInfo.manufacturer,
                    family = cpuInfo.family,
                    model = cpuInfo.model,
                    stepping = cpuInfo.stepping,
                    physical_cores = cpuInfo.physicalCores,
                    logical_cores = cpuInfo.logicalCores,
                    max_clock_speed_mhz = cpuInfo.maxClockSpeedMhz,
                    l2_cache_size_kb = cpuInfo.l2CacheSizeKb,
                    l3_cache_size_kb = cpuInfo.l3CacheSizeKb
                },
                
                // 内存信息
                memory = new
                {
                    total_physical_mb = memoryInfo.totalPhysicalMb,
                    total_slots = memoryInfo.totalSlots,
                    modules = memoryInfo.modules
                },
                
                // 图形设备信息
                graphics = graphicsInfo,
                
                // BIOS/固件信息
                firmware = new
                {
                    manufacturer = biosInfo.manufacturer,
                    version = biosInfo.version,
                    release_date = biosInfo.releaseDate,
                    smbios_version = biosInfo.smbiosVersion
                },
                
                // 网络标识信息
                network_identity = new
                {
                    hostname = networkInfo.hostname,
                    domain = networkInfo.domain,
                    primary_mac_address = networkInfo.primaryMacAddress,
                    local_ip_addresses = networkInfo.localIpAddresses,
                    public_ip_address = networkInfo.publicIpAddress
                }
            };
        }

        /// <summary>
        /// 获取机器硬件信息
        /// </summary>
        private static (string? manufacturer, string? model, string? serialNumber, string? uuid, string? chassisType) GetMachineInfo()
        {
            string? manufacturer = null, model = null, serialNumber = null, uuid = null, chassisType = null;

            try
            {
                // 计算机系统信息
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
                foreach (ManagementObject mo in searcher.Get())
                {
                    manufacturer = mo["Manufacturer"]?.ToString()?.Trim();
                    model = mo["Model"]?.ToString()?.Trim();
                    break;
                }

                // 计算机系统产品信息（更详细的型号和序列号）
                using var productSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystemProduct");
                foreach (ManagementObject mo in productSearcher.Get())
                {
                    var productName = mo["Name"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(productName) && string.IsNullOrEmpty(model))
                        model = productName;
                    
                    serialNumber = mo["IdentifyingNumber"]?.ToString()?.Trim();
                    uuid = mo["UUID"]?.ToString()?.Trim();
                    break;
                }

                // 机箱类型信息
                using var chassisSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_SystemEnclosure");
                foreach (ManagementObject mo in chassisSearcher.Get())
                {
                    var chassisTypes = mo["ChassisTypes"] as ushort[];
                    if (chassisTypes?.Length > 0)
                    {
                        chassisType = MapChassisType(chassisTypes[0]);
                    }
                    break;
                }
            }
            catch { /* 忽略WMI查询错误 */ }

            return (manufacturer, model, serialNumber, uuid, chassisType);
        }

        /// <summary>
        /// 获取操作系统信息
        /// </summary>
        private static (string? name, string? version, string? buildNumber, string? architecture, 
                       string? installDate, string? lastBootTime, double? uptimeHours) GetOperatingSystemInfo()
        {
            string? name = null, version = null, buildNumber = null, architecture = null;
            string? installDate = null, lastBootTime = null;
            double? uptimeHours = null;

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem");
                foreach (ManagementObject mo in searcher.Get())
                {
                    name = mo["Caption"]?.ToString()?.Trim();
                    version = mo["Version"]?.ToString()?.Trim();
                    buildNumber = mo["BuildNumber"]?.ToString()?.Trim();
                    architecture = mo["OSArchitecture"]?.ToString()?.Trim();
                    
                    // 安装日期
                    var installDateWmi = mo["InstallDate"]?.ToString();
                    if (!string.IsNullOrEmpty(installDateWmi))
                    {
                        installDate = ConvertWmiDateTime(installDateWmi);
                    }
                    
                    // 最后启动时间
                    var lastBootWmi = mo["LastBootUpTime"]?.ToString();
                    if (!string.IsNullOrEmpty(lastBootWmi))
                    {
                        lastBootTime = ConvertWmiDateTime(lastBootWmi);
                        // 计算运行时间
                        if (DateTime.TryParse(lastBootTime, out var bootTime))
                        {
                            uptimeHours = (DateTime.Now - bootTime).TotalHours;
                        }
                    }
                    break;
                }
            }
            catch { /* 忽略WMI查询错误 */ }

            return (name, version, buildNumber, architecture, installDate, lastBootTime, uptimeHours);
        }

        /// <summary>
        /// 获取CPU详细信息
        /// </summary>
        private static (string? name, string? manufacturer, int? family, int? model, int? stepping,
                       int? physicalCores, int? logicalCores, int? maxClockSpeedMhz, 
                       int? l2CacheSizeKb, int? l3CacheSizeKb) GetCpuInfo()
        {
            string? name = null, manufacturer = null;
            int? family = null, model = null, stepping = null;
            int? physicalCores = null, logicalCores = null, maxClockSpeedMhz = null;
            int? l2CacheSizeKb = null, l3CacheSizeKb = null;

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor");
                foreach (ManagementObject mo in searcher.Get())
                {
                    name = mo["Name"]?.ToString()?.Trim();
                    manufacturer = mo["Manufacturer"]?.ToString()?.Trim();
                    
                    if (int.TryParse(mo["Family"]?.ToString(), out var f)) family = f;
                    if (int.TryParse(mo["Model"]?.ToString(), out var m)) model = m;
                    if (int.TryParse(mo["Stepping"]?.ToString(), out var s)) stepping = s;
                    if (int.TryParse(mo["NumberOfCores"]?.ToString(), out var pc)) physicalCores = pc;
                    if (int.TryParse(mo["NumberOfLogicalProcessors"]?.ToString(), out var lc)) logicalCores = lc;
                    if (int.TryParse(mo["MaxClockSpeed"]?.ToString(), out var mcs)) maxClockSpeedMhz = mcs;
                    if (int.TryParse(mo["L2CacheSize"]?.ToString(), out var l2)) l2CacheSizeKb = l2;
                    if (int.TryParse(mo["L3CacheSize"]?.ToString(), out var l3)) l3CacheSizeKb = l3;
                    
                    break; // 只取第一个CPU信息
                }
            }
            catch { /* 忽略WMI查询错误 */ }

            return (name, manufacturer, family, model, stepping, physicalCores, logicalCores, 
                   maxClockSpeedMhz, l2CacheSizeKb, l3CacheSizeKb);
        }

        /// <summary>
        /// 获取内存详细信息
        /// </summary>
        private static (long? totalPhysicalMb, int? totalSlots, object[]? modules) GetMemoryInfo()
        {
            long? totalPhysicalMb = null;
            int? totalSlots = null;
            var modules = new List<object>();

            try
            {
                // 获取物理内存总量
                using var systemSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
                foreach (ManagementObject mo in systemSearcher.Get())
                {
                    if (long.TryParse(mo["TotalPhysicalMemory"]?.ToString(), out var total))
                    {
                        totalPhysicalMb = total / (1024 * 1024);
                    }
                    break;
                }

                // 获取内存插槽和模块信息
                using var memorySearcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory");
                foreach (ManagementObject mo in memorySearcher.Get())
                {
                    var capacity = mo["Capacity"]?.ToString();
                    var speed = mo["Speed"]?.ToString();
                    var manufacturer = mo["Manufacturer"]?.ToString()?.Trim();
                    var partNumber = mo["PartNumber"]?.ToString()?.Trim();
                    var deviceLocator = mo["DeviceLocator"]?.ToString()?.Trim();

                    modules.Add(new
                    {
                        device_locator = deviceLocator,
                        capacity_mb = long.TryParse(capacity, out var cap) ? cap / (1024 * 1024) : (long?)null,
                        speed_mhz = int.TryParse(speed, out var spd) ? spd : (int?)null,
                        manufacturer = manufacturer,
                        part_number = partNumber
                    });
                }

                totalSlots = modules.Count;
            }
            catch { /* 忽略WMI查询错误 */ }

            return (totalPhysicalMb, totalSlots, modules.ToArray());
        }

        /// <summary>
        /// 获取图形设备信息
        /// </summary>
        private static object[] GetGraphicsInfo()
        {
            var graphics = new List<object>();

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                foreach (ManagementObject mo in searcher.Get())
                {
                    var name = mo["Name"]?.ToString()?.Trim();
                    var videoProcessor = mo["VideoProcessor"]?.ToString()?.Trim();
                    var adapterRam = mo["AdapterRAM"]?.ToString();
                    var driverVersion = mo["DriverVersion"]?.ToString()?.Trim();
                    var driverDate = mo["DriverDate"]?.ToString();

                    graphics.Add(new
                    {
                        name = name,
                        video_processor = videoProcessor,
                        adapter_ram_mb = long.TryParse(adapterRam, out var ram) ? ram / (1024 * 1024) : (long?)null,
                        driver_version = driverVersion,
                        driver_date = ConvertWmiDateTime(driverDate)
                    });
                }
            }
            catch { /* 忽略WMI查询错误 */ }

            return graphics.ToArray();
        }

        /// <summary>
        /// 获取BIOS/固件信息
        /// </summary>
        private static (string? manufacturer, string? version, string? releaseDate, string? smbiosVersion) GetBiosInfo()
        {
            string? manufacturer = null, version = null, releaseDate = null, smbiosVersion = null;

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BIOS");
                foreach (ManagementObject mo in searcher.Get())
                {
                    manufacturer = mo["Manufacturer"]?.ToString()?.Trim();
                    version = mo["SMBIOSBIOSVersion"]?.ToString()?.Trim();
                    smbiosVersion = mo["SMBIOSMajorVersion"]?.ToString() + "." + mo["SMBIOSMinorVersion"]?.ToString();
                    
                    var releaseDateWmi = mo["ReleaseDate"]?.ToString();
                    if (!string.IsNullOrEmpty(releaseDateWmi))
                    {
                        releaseDate = ConvertWmiDateTime(releaseDateWmi);
                    }
                    break;
                }
            }
            catch { /* 忽略WMI查询错误 */ }

            return (manufacturer, version, releaseDate, smbiosVersion);
        }

        /// <summary>
        /// 获取网络标识信息
        /// </summary>
        private static (string? hostname, string? domain, string? primaryMacAddress, 
                       string[]? localIpAddresses, string? publicIpAddress) GetNetworkIdentification()
        {
            string? hostname = null, domain = null, primaryMacAddress = null, publicIpAddress = null;
            var localIps = new List<string>();

            try
            {
                // 主机名和域
                hostname = Environment.MachineName;
                domain = Environment.UserDomainName;

                // 本地IP地址
                var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in hostEntry.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
                        ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    {
                        localIps.Add(ip.ToString());
                    }
                }

                // 主要MAC地址（第一个活动的物理网络接口）
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var iface in interfaces.Where(i => i.OperationalStatus == OperationalStatus.Up && 
                                                          i.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    var mac = iface.GetPhysicalAddress().ToString();
                    if (!string.IsNullOrEmpty(mac) && mac != "000000000000")
                    {
                        // 格式化MAC地址为 XX:XX:XX:XX:XX:XX
                        if (mac.Length == 12)
                        {
                            primaryMacAddress = string.Join(":", Enumerable.Range(0, 6)
                                .Select(i => mac.Substring(i * 2, 2)));
                        }
                        break;
                    }
                }

                // 公网IP地址（尝试快速获取，失败则跳过）
                try
                {
                    using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
                    {
                        AllowAutoRedirect = true,
                        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
                    })
                    {
                        Timeout = TimeSpan.FromMilliseconds(5000)
                    };
                    // 同步环境下安全地等待结果
                    var resp = http.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult();
                    publicIpAddress = resp?.Trim();
                }
                catch { /* 忽略公网IP获取失败 */ }
            }
            catch { /* 忽略网络信息获取错误 */ }

            return (hostname, domain, primaryMacAddress, localIps.ToArray(), publicIpAddress);
        }

        /// <summary>
        /// 创建故障备用数据
        /// </summary>
        private static object CreateFallbackData(string error)
        {
            return new
            {
                machine = new
                {
                    manufacturer = Environment.MachineName,
                    model = "Unknown",
                    serial_number = (string?)null,
                    uuid = (string?)null,
                    chassis_type = "Unknown"
                },
                operating_system = new
                {
                    name = Environment.OSVersion.VersionString,
                    version = Environment.OSVersion.Version.ToString(),
                    build_number = (string?)null,
                    architecture = RuntimeInformation.OSArchitecture.ToString(),
                    install_date = (string?)null,
                    last_boot_time = (string?)null,
                    uptime_hours = Environment.TickCount64 / (1000.0 * 3600.0)
                },
                processor = new
                {
                    name = "Unknown",
                    manufacturer = (string?)null,
                    family = (int?)null,
                    model = (int?)null,
                    stepping = (int?)null,
                    physical_cores = Environment.ProcessorCount,
                    logical_cores = Environment.ProcessorCount,
                    max_clock_speed_mhz = (int?)null,
                    l2_cache_size_kb = (int?)null,
                    l3_cache_size_kb = (int?)null
                },
                memory = new
                {
                    total_physical_mb = (long?)null,
                    total_slots = (int?)null,
                    modules = Array.Empty<object>()
                },
                graphics = Array.Empty<object>(),
                firmware = new
                {
                    manufacturer = (string?)null,
                    version = (string?)null,
                    release_date = (string?)null,
                    smbios_version = (string?)null
                },
                network_identity = new
                {
                    hostname = Environment.MachineName,
                    domain = Environment.UserDomainName,
                    primary_mac_address = (string?)null,
                    local_ip_addresses = Array.Empty<string>(),
                    public_ip_address = (string?)null
                },
                error = error
            };
        }

        /// <summary>
        /// 转换WMI日期时间格式
        /// </summary>
        private static string? ConvertWmiDateTime(string? wmiDateTime)
        {
            if (string.IsNullOrEmpty(wmiDateTime)) return null;
            
            try
            {
                // WMI日期格式：20231225143022.000000+480
                if (wmiDateTime.Length >= 14)
                {
                    var year = int.Parse(wmiDateTime.Substring(0, 4));
                    var month = int.Parse(wmiDateTime.Substring(4, 2));
                    var day = int.Parse(wmiDateTime.Substring(6, 2));
                    var hour = int.Parse(wmiDateTime.Substring(8, 2));
                    var minute = int.Parse(wmiDateTime.Substring(10, 2));
                    var second = int.Parse(wmiDateTime.Substring(12, 2));
                    
                    var dateTime = new DateTime(year, month, day, hour, minute, second);
                    return dateTime.ToString("yyyy-MM-dd HH:mm:ss");
                }
            }
            catch { /* 忽略日期转换错误 */ }
            
            return wmiDateTime;
        }

        /// <summary>
        /// 映射机箱类型代码到描述
        /// </summary>
        private static string MapChassisType(ushort chassisType)
        {
            return chassisType switch
            {
                1 => "Other",
                3 => "Desktop",
                4 => "Low Profile Desktop",
                6 => "Mini Tower",
                7 => "Tower",
                8 => "Portable",
                9 => "Laptop",
                10 => "Notebook",
                11 => "Hand Held",
                12 => "Docking Station",
                13 => "All in One",
                14 => "Sub Notebook",
                15 => "Space-saving",
                16 => "Lunch Box",
                17 => "Main Server Chassis",
                18 => "Expansion Chassis",
                19 => "SubChassis",
                20 => "Bus Expansion Chassis",
                21 => "Peripheral Chassis",
                22 => "RAID Chassis",
                23 => "Rack Mount Chassis",
                24 => "Sealed-case PC",
                30 => "Tablet",
                31 => "Convertible",
                32 => "Detachable",
                _ => $"Unknown ({chassisType})"
            };
        }
    }
}