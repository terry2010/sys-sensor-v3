using System;
using System.Threading.Tasks;
using Xunit;
using SystemMonitor.Service.Services.Collectors;

namespace SystemMonitor.Tests
{
    /// <summary>
    /// SystemInfoCollector 单元测试
    /// 测试系统信息采集器的基本功能和数据结构
    /// </summary>
    public class SystemInfoCollectorTests
    {
        [Fact]
        public void Name_ShouldReturnCorrectName()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var name = collector.Name;

            // Assert
            Assert.Equal("system_info", name);
        }

        [Fact]
        public void Collect_ShouldReturnNonNullResult()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var result = collector.Collect();

            // Assert
            Assert.NotNull(result);
        }

        [Fact]
        public void Collect_ShouldReturnValidDataStructure()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var result = collector.Collect();

            // Assert
            Assert.NotNull(result);
            
            // 使用反射验证数据结构
            var resultType = result.GetType();
            
            // 验证主要属性存在
            Assert.NotNull(resultType.GetProperty("machine"));
            Assert.NotNull(resultType.GetProperty("operating_system"));
            Assert.NotNull(resultType.GetProperty("processor"));
            Assert.NotNull(resultType.GetProperty("memory"));
            Assert.NotNull(resultType.GetProperty("graphics"));
            Assert.NotNull(resultType.GetProperty("firmware"));
            Assert.NotNull(resultType.GetProperty("network_identity"));
        }

        [Fact]
        public void Collect_ShouldReturnMachineInfo()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var result = collector.Collect();
            dynamic data = result;

            // Assert
            Assert.NotNull(data.machine);
            
            // 验证机器信息字段存在（值可能为null）
            var machine = data.machine;
            Assert.True(HasProperty(machine, "manufacturer"));
            Assert.True(HasProperty(machine, "model"));
            Assert.True(HasProperty(machine, "serial_number"));
            Assert.True(HasProperty(machine, "uuid"));
            Assert.True(HasProperty(machine, "chassis_type"));
        }

        [Fact]
        public void Collect_ShouldReturnOperatingSystemInfo()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var result = collector.Collect();
            dynamic data = result;

            // Assert
            Assert.NotNull(data.operating_system);
            
            var os = data.operating_system;
            Assert.True(HasProperty(os, "name"));
            Assert.True(HasProperty(os, "version"));
            Assert.True(HasProperty(os, "build_number"));
            Assert.True(HasProperty(os, "architecture"));
            Assert.True(HasProperty(os, "install_date"));
            Assert.True(HasProperty(os, "last_boot_time"));
            Assert.True(HasProperty(os, "uptime_hours"));
        }

        [Fact]
        public void Collect_ShouldReturnProcessorInfo()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var result = collector.Collect();
            dynamic data = result;

            // Assert
            Assert.NotNull(data.processor);
            
            var processor = data.processor;
            Assert.True(HasProperty(processor, "name"));
            Assert.True(HasProperty(processor, "manufacturer"));
            Assert.True(HasProperty(processor, "family"));
            Assert.True(HasProperty(processor, "model"));
            Assert.True(HasProperty(processor, "stepping"));
            Assert.True(HasProperty(processor, "physical_cores"));
            Assert.True(HasProperty(processor, "logical_cores"));
            Assert.True(HasProperty(processor, "max_clock_speed_mhz"));
            Assert.True(HasProperty(processor, "l2_cache_size_kb"));
            Assert.True(HasProperty(processor, "l3_cache_size_kb"));
        }

        [Fact]
        public void Collect_ShouldReturnMemoryInfo()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var result = collector.Collect();
            dynamic data = result;

            // Assert
            Assert.NotNull(data.memory);
            
            var memory = data.memory;
            Assert.True(HasProperty(memory, "total_physical_mb"));
            Assert.True(HasProperty(memory, "total_slots"));
            Assert.True(HasProperty(memory, "modules"));
            
            // 验证modules是数组
            Assert.NotNull(memory.modules);
        }

        [Fact]
        public void Collect_ShouldReturnGraphicsInfo()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var result = collector.Collect();
            dynamic data = result;

            // Assert
            Assert.NotNull(data.graphics);
            
            // graphics应该是数组
            var graphics = data.graphics as Array;
            Assert.NotNull(graphics);
        }

        [Fact]
        public void Collect_ShouldReturnFirmwareInfo()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var result = collector.Collect();
            dynamic data = result;

            // Assert
            Assert.NotNull(data.firmware);
            
            var firmware = data.firmware;
            Assert.True(HasProperty(firmware, "manufacturer"));
            Assert.True(HasProperty(firmware, "version"));
            Assert.True(HasProperty(firmware, "release_date"));
            Assert.True(HasProperty(firmware, "smbios_version"));
        }

        [Fact]
        public void Collect_ShouldReturnNetworkIdentityInfo()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var result = collector.Collect();
            dynamic data = result;

            // Assert
            Assert.NotNull(data.network_identity);
            
            var networkIdentity = data.network_identity;
            Assert.True(HasProperty(networkIdentity, "hostname"));
            Assert.True(HasProperty(networkIdentity, "domain"));
            Assert.True(HasProperty(networkIdentity, "primary_mac_address"));
            Assert.True(HasProperty(networkIdentity, "local_ip_addresses"));
            Assert.True(HasProperty(networkIdentity, "public_ip_address"));
            
            // hostname应该有值（至少是机器名）
            Assert.NotNull(networkIdentity.hostname);
            Assert.NotEmpty(networkIdentity.hostname.ToString());
        }

        [Fact]
        public void Collect_ShouldUseCache()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var result1 = collector.Collect();
            var result2 = collector.Collect();

            // Assert
            Assert.NotNull(result1);
            Assert.NotNull(result2);
            
            // 由于使用缓存，两次调用应该返回相同的实例（引用相等）
            Assert.Same(result1, result2);
        }

        [Fact]
        public void Collect_ShouldHandleWmiErrors()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act & Assert
            // 即使WMI查询失败，也应该返回fallback数据而不是抛出异常
            var result = collector.Collect();
            Assert.NotNull(result);
            
            // 至少应该有基本的hostname信息
            dynamic data = result;
            Assert.NotNull(data.network_identity);
            Assert.NotNull(data.network_identity.hostname);
        }

        [Fact]
        public void Collect_ShouldFollowSnakeCaseNaming()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Act
            var result = collector.Collect();
            dynamic data = result;

            // Assert
            // 验证字段名使用snake_case格式
            Assert.NotNull(data.operating_system);
            Assert.NotNull(data.network_identity);
            
            var processor = data.processor;
            Assert.True(HasProperty(processor, "physical_cores"));
            Assert.True(HasProperty(processor, "logical_cores"));
            Assert.True(HasProperty(processor, "max_clock_speed_mhz"));
            Assert.True(HasProperty(processor, "l2_cache_size_kb"));
            Assert.True(HasProperty(processor, "l3_cache_size_kb"));
            
            var memory = data.memory;
            Assert.True(HasProperty(memory, "total_physical_mb"));
            Assert.True(HasProperty(memory, "total_slots"));
            
            var networkIdentity = data.network_identity;
            Assert.True(HasProperty(networkIdentity, "primary_mac_address"));
            Assert.True(HasProperty(networkIdentity, "local_ip_addresses"));
            Assert.True(HasProperty(networkIdentity, "public_ip_address"));
        }

        /// <summary>
        /// 验证动态对象是否包含指定属性
        /// </summary>
        private static bool HasProperty(object obj, string propertyName)
        {
            if (obj == null) return false;
            
            try
            {
                var property = obj.GetType().GetProperty(propertyName);
                return property != null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// SystemInfoCollector 集成测试
    /// 测试与其他组件的集成
    /// </summary>
    public class SystemInfoCollectorIntegrationTests
    {
        [Fact]
        public void Collector_ShouldBeRegisteredInMetricsRegistry()
        {
            // Act
            var collectors = MetricsRegistry.Collectors;

            // Assert
            Assert.Contains(collectors, c => c.Name == "system_info");
        }

        [Fact]
        public void Collector_ShouldImplementIMetricsCollector()
        {
            // Arrange
            var collector = new SystemInfoCollector();

            // Assert
            Assert.IsAssignableFrom<IMetricsCollector>(collector);
        }

        [Fact]
        public async Task Collector_ShouldWorkInParallelWithOtherCollectors()
        {
            // Arrange
            var systemInfoCollector = new SystemInfoCollector();
            var cpuCollector = new CpuCollector();
            var memoryCollector = new MemoryCollector();

            // Act
            var tasks = new[]
            {
                Task.Run(() => systemInfoCollector.Collect()),
                Task.Run(() => cpuCollector.Collect()),
                Task.Run(() => memoryCollector.Collect())
            };

            var results = await Task.WhenAll(tasks);

            // Assert
            Assert.All(results, result => Assert.NotNull(result));
            
            // 验证系统信息采集器的结果
            var systemInfoResult = results[0];
            dynamic data = systemInfoResult;
            Assert.NotNull(data.machine);
            Assert.NotNull(data.operating_system);
            Assert.NotNull(data.processor);
        }
    }
}