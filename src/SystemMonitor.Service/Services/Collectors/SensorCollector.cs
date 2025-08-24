using System;
namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class SensorCollector : IMetricsCollector
    {
        public string Name => "sensor";
        public object? Collect()
        {
            // 读取硬件传感器：通过 SensorsProvider（默认 LhmSensors.Instance）
            // 输出字段遵循 snake_case，对外单位：温度=摄氏度、功率=瓦、风扇=RPM
            try
            {
                var (pkgTemp, cores, pkgPower, fans) = SensorsProvider.Current.Read();

                // 可选的完整传感器转储（仅当设置了环境变量，避免常态下过大 payload）
                object? dump = null;
                try
                {
                    var dumpAll = System.Environment.GetEnvironmentVariable("SYS_SENSOR_DUMP_ALL");
                    if (!string.IsNullOrEmpty(dumpAll) && dumpAll == "1")
                    {
                        var arr = SensorsProvider.Current.DumpAll();
                        dump = new
                        {
                            sensors = arr
                        };
                    }
                }
                catch { /* ignore dump error */ }

                // 数值规范化：温度/功率保留 1 位小数；风扇 RPM 取整且非负
                static double? R1(double? v)
                {
                    if (!v.HasValue) return null;
                    var x = Math.Round(v.Value, 1);
                    if (double.IsNaN(x) || double.IsInfinity(x)) return null;
                    return x;
                }
                static double[]? R1Arr(double?[]? arr)
                {
                    if (arr == null || arr.Length == 0) return null;
                    var list = new System.Collections.Generic.List<double>();
                    foreach (var v in arr)
                    {
                        var r = R1(v);
                        if (r.HasValue) list.Add(r.Value);
                    }
                    return list.Count > 0 ? list.ToArray() : null;
                }
                static int[]? FixRpm(int?[]? arr)
                {
                    if (arr == null || arr.Length == 0) return null;
                    var list = new System.Collections.Generic.List<int>();
                    foreach (var v in arr)
                    {
                        if (v.HasValue) list.Add(Math.Max(0, v.Value));
                    }
                    return list.Count > 0 ? list.ToArray() : null;
                }

                // 进一步基于 DumpAll 聚合：温度/风扇详情/功率/电压/负载/时钟/电流/控制(占空比)/流量/液位/因子
                SystemMonitor.Service.Services.LhmSensorDto[] all = Array.Empty<SystemMonitor.Service.Services.LhmSensorDto>();
                try { all = SensorsProvider.Current.DumpAll(); } catch { }

                // temperatures: [{ key, c }]
                var temps = new System.Collections.Generic.List<object>();
                try
                {
                    foreach (var s in all)
                    {
                        if (!string.Equals(s.sensor_type, "Temperature", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!s.value.HasValue) continue;
                        var v = R1(s.value);
                        if (!v.HasValue) continue;
                        var key = string.Join("/", new[] { s.hw_type, s.hw_name, s.sensor_name }
                            ).Replace("\\", "/");
                        temps.Add(new { key, c = v.Value });
                    }
                }
                catch { }

                // fan_details: [{ key, rpm }]
                var fanDetails = new System.Collections.Generic.List<object>();
                // fan_control_details: [{ key, percent }]
                var fanCtrlDetails = new System.Collections.Generic.List<object>();
                try
                {
                    foreach (var s in all)
                    {
                        if (!string.Equals(s.sensor_type, "Fan", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!s.value.HasValue) continue;
                        var rpm = (int)Math.Max(0, Math.Round(s.value.Value));
                        var key = string.Join("/", new[] { s.hw_type, s.hw_name, s.sensor_name }
                            ).Replace("\\", "/");
                        fanDetails.Add(new { key, rpm });
                    }
                }
                catch { }

                // controls_percent: { key: number }，并另外输出 fan_control_details 便于前端直观展示风扇占空比
                var controls = new System.Collections.Generic.Dictionary<string, double>();
                try
                {
                    foreach (var s in all)
                    {
                        if (!string.Equals(s.sensor_type, "Control", StringComparison.OrdinalIgnoreCase)) continue;
                        var v = R1(s.value);
                        if (!v.HasValue) continue;
                        var key = string.Join("/", new[] { s.hw_type, s.hw_name, s.sensor_name }
                            ).Replace("\\", "/");
                        if (!controls.ContainsKey(key)) controls[key] = v.Value;
                        // 简单启发式：名称包含 Fan 的控制视为风扇占空比
                        if ((s.sensor_name ?? string.Empty).IndexOf("Fan", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            fanCtrlDetails.Add(new { key, percent = v.Value });
                        }
                    }
                }
                catch { }

                // powers_w: { key: number }
                var powers = new System.Collections.Generic.Dictionary<string, double>();
                try
                {
                    foreach (var s in all)
                    {
                        if (!string.Equals(s.sensor_type, "Power", StringComparison.OrdinalIgnoreCase)) continue;
                        var v = R1(s.value);
                        if (!v.HasValue) continue;
                        var key = string.Join("/", new[] { s.hw_type, s.hw_name, s.sensor_name }
                            ).Replace("\\", "/");
                        if (!powers.ContainsKey(key)) powers[key] = v.Value;
                    }
                }
                catch { }

                // voltages_v: { key: number }
                var volts = new System.Collections.Generic.Dictionary<string, double>();
                try
                {
                    foreach (var s in all)
                    {
                        if (!string.Equals(s.sensor_type, "Voltage", StringComparison.OrdinalIgnoreCase)) continue;
                        var v = R1(s.value);
                        if (!v.HasValue) continue;
                        var key = string.Join("/", new[] { s.hw_type, s.hw_name, s.sensor_name }
                            ).Replace("\\", "/");
                        if (!volts.ContainsKey(key)) volts[key] = v.Value;
                    }
                }
                catch { }

                // loads_percent: { key: number }
                var loads = new System.Collections.Generic.Dictionary<string, double>();
                try
                {
                    foreach (var s in all)
                    {
                        if (!string.Equals(s.sensor_type, "Load", StringComparison.OrdinalIgnoreCase)) continue;
                        var v = R1(s.value);
                        if (!v.HasValue) continue;
                        var key = string.Join("/", new[] { s.hw_type, s.hw_name, s.sensor_name }
                            ).Replace("\\", "/");
                        if (!loads.ContainsKey(key)) loads[key] = v.Value;
                    }
                }
                catch { }

                // clocks_mhz: { key: number }
                var clocks = new System.Collections.Generic.Dictionary<string, double>();
                try
                {
                    foreach (var s in all)
                    {
                        if (!string.Equals(s.sensor_type, "Clock", StringComparison.OrdinalIgnoreCase)) continue;
                        var v = R1(s.value);
                        if (!v.HasValue) continue;
                        var key = string.Join("/", new[] { s.hw_type, s.hw_name, s.sensor_name }
                            ).Replace("\\", "/");
                        if (!clocks.ContainsKey(key)) clocks[key] = v.Value;
                    }
                }
                catch { }

                // currents_a: { key: number }
                var currents = new System.Collections.Generic.Dictionary<string, double>();
                try
                {
                    foreach (var s in all)
                    {
                        if (!string.Equals(s.sensor_type, "Current", StringComparison.OrdinalIgnoreCase)) continue;
                        var v = R1(s.value);
                        if (!v.HasValue) continue;
                        var key = string.Join("/", new[] { s.hw_type, s.hw_name, s.sensor_name }
                            ).Replace("\\", "/");
                        if (!currents.ContainsKey(key)) currents[key] = v.Value;
                    }
                }
                catch { }

                // flows_lpm: { key: number }（如水冷流量，单位依硬件实现，通常 L/min）
                var flows = new System.Collections.Generic.Dictionary<string, double>();
                try
                {
                    foreach (var s in all)
                    {
                        if (!string.Equals(s.sensor_type, "Flow", StringComparison.OrdinalIgnoreCase)) continue;
                        var v = R1(s.value);
                        if (!v.HasValue) continue;
                        var key = string.Join("/", new[] { s.hw_type, s.hw_name, s.sensor_name }
                            ).Replace("\\", "/");
                        if (!flows.ContainsKey(key)) flows[key] = v.Value;
                    }
                }
                catch { }

                // levels_percent: { key: number }（如水箱液位）
                var levels = new System.Collections.Generic.Dictionary<string, double>();
                try
                {
                    foreach (var s in all)
                    {
                        if (!string.Equals(s.sensor_type, "Level", StringComparison.OrdinalIgnoreCase)) continue;
                        var v = R1(s.value);
                        if (!v.HasValue) continue;
                        var key = string.Join("/", new[] { s.hw_type, s.hw_name, s.sensor_name }
                            ).Replace("\\", "/");
                        if (!levels.ContainsKey(key)) levels[key] = v.Value;
                    }
                }
                catch { }

                // factors: { key: number }（无单位的比例型）
                var factors = new System.Collections.Generic.Dictionary<string, double>();
                try
                {
                    foreach (var s in all)
                    {
                        if (!string.Equals(s.sensor_type, "Factor", StringComparison.OrdinalIgnoreCase)) continue;
                        var v = R1(s.value);
                        if (!v.HasValue) continue;
                        var key = string.Join("/", new[] { s.hw_type, s.hw_name, s.sensor_name }
                            ).Replace("\\", "/");
                        if (!factors.ContainsKey(key)) factors[key] = v.Value;
                    }
                }
                catch { }

                var result = new
                {
                    cpu = new
                    {
                        package_temp_c = R1(pkgTemp),
                        core_temps_c = R1Arr(cores),
                        package_power_w = R1(pkgPower)
                    },
                    fan_rpm = FixRpm(fans),
                    fan_count = fanDetails.Count,
                    temperatures = temps.Count > 0 ? temps.ToArray() : Array.Empty<object>(),
                    fan_details = fanDetails.Count > 0 ? fanDetails.ToArray() : Array.Empty<object>(),
                    fan_control_details = fanCtrlDetails.Count > 0 ? fanCtrlDetails.ToArray() : Array.Empty<object>(),
                    powers_w = powers,
                    voltages_v = volts,
                    loads_percent = loads,
                    clocks_mhz = clocks,
                    currents_a = currents,
                    controls_percent = controls,
                    flows_lpm = flows,
                    levels_percent = levels,
                    factors = factors,
                    dump_all = dump
                };

                return result;
            }
            catch
            {
                // 容错：不可用时返回空对象，避免影响其他模块
                return new
                {
                    cpu = new { package_temp_c = (double?)null, core_temps_c = (double[]?)null, package_power_w = (double?)null },
                    fan_rpm = (int[]?)null,
                    temperatures = Array.Empty<object>(),
                    fan_details = Array.Empty<object>(),
                    powers_w = new System.Collections.Generic.Dictionary<string, double>(),
                    voltages_v = new System.Collections.Generic.Dictionary<string, double>()
                };
            }
        }
    }
}
