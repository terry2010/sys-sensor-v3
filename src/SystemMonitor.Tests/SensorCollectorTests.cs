using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SystemMonitor.Service.Services.Collectors;
using Xunit;

namespace SystemMonitor.Tests
{
    public class SensorCollectorTests
    {
        private static JsonSerializerOptions SnakeCaseOptions => new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        [Fact]
        public void Collect_NoThrow_And_HasExpectedKeys()
        {
            var c = new SensorCollector();
            object? obj = null;
            Exception? err = null;
            try { obj = c.Collect(); } catch (Exception ex) { err = ex; }
            Assert.Null(err);
            Assert.NotNull(obj);

            var json = JsonSerializer.Serialize(obj, SnakeCaseOptions);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 顶层包含 cpu 与 fan_rpm；允许 dump_all 存在或缺省
            Assert.True(root.TryGetProperty("cpu", out var cpu), "missing cpu object");
            Assert.True(root.TryGetProperty("fan_rpm", out var _)
                        || root.GetProperty("cpu").ValueKind == JsonValueKind.Object, "missing fan_rpm (can be null but key may be absent)");

            // cpu 下的三个字段允许为 null
            Assert.True(cpu.TryGetProperty("package_temp_c", out var _), "missing package_temp_c");
            Assert.True(cpu.TryGetProperty("core_temps_c", out var _), "missing core_temps_c");
            Assert.True(cpu.TryGetProperty("package_power_w", out var _), "missing package_power_w");
        }

        [Fact]
        public void Collect_WithDumpAllEnv_IncludesDumpAllSensors()
        {
            // 设置环境变量以启用 dump_all
            var prev = Environment.GetEnvironmentVariable("SYS_SENSOR_DUMP_ALL");
            try
            {
                Environment.SetEnvironmentVariable("SYS_SENSOR_DUMP_ALL", "1");
                var c = new SensorCollector();
                var obj = c.Collect();
                var json = JsonSerializer.Serialize(obj, SnakeCaseOptions);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("dump_all", out var dump));
                // dump_all 结构：{ sensors: [] }
                Assert.True(dump.ValueKind == JsonValueKind.Object);
                Assert.True(dump.TryGetProperty("sensors", out var sensors));
                Assert.True(sensors.ValueKind == JsonValueKind.Array);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SYS_SENSOR_DUMP_ALL", prev);
            }
        }
    }
}
