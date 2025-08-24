using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using static SystemMonitor.Service.Services.Win32Interop;

namespace SystemMonitor.Service.Services.Collectors
{
    /// <summary>
    /// 电源/电池采集器。
    /// 数据来源：
    /// 1) Win32 GetSystemPowerStatus（基础供电状态/电量/剩余时间）
    /// 2) WMI root\\WMI（BatteryStatus/BatteryStaticData/BatteryFullChargedCapacity/BatteryCycleCount 等，按可用性补齐）
    /// </summary>
    internal sealed class PowerCollector : IMetricsCollector
    {
        public string Name => "power";

        // 会话内缓存，节流 WMI 访问
        private static readonly object _lock = new();
        private static long _lastTs;
        private static object? _lastPayload;

        public object? Collect()
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                lock (_lock)
                {
                    if (_lastPayload != null && now - _lastTs <= 400)
                    {
                        return _lastPayload;
                    }
                }

                var payload = BuildPayload();
                lock (_lock)
                {
                    _lastTs = now;
                    _lastPayload = payload;
                }
                return payload;
            }
            catch
            {
                return new
                {
                    battery = new
                    {
                        percentage = (double?)null,
                        state = "unknown",
                        time_remaining_min = (int?)null,
                        time_to_full_min = (int?)null,
                        ac_line_online = (bool?)null,
                        time_on_battery_sec = (int?)null,
                        temperature_c = (double?)null,
                        cycle_count = (int?)null,
                        condition = (string?)null,
                        full_charge_capacity_mah = (double?)null,
                        design_capacity_mah = (double?)null,
                        voltage_mv = (double?)null,
                        current_ma = (double?)null,
                        power_w = (double?)null,
                        manufacturer = (string?)null,
                        serial_number = (string?)null,
                        manufacture_date = (string?)null
                    },
                    adapter = (object?)null,
                    ups = (object?)null
                };
            }
        }

        private static object BuildPayload()
        {
            // 1) 基础：GetSystemPowerStatus
            double? pct = null; bool? acOnline = null; int? remainMin = null; int? toFullMin = null; string state = "unknown";
            try
            {
                if (GetSystemPowerStatus(out var sps))
                {
                    pct = sps.BatteryLifePercent == 255 ? (double?)null : sps.BatteryLifePercent;
                    acOnline = sps.ACLineStatus == 0 ? false : sps.ACLineStatus == 1 ? true : (bool?)null;
                    remainMin = sps.BatteryLifeTime >= 0 ? (int?)Math.Max(0, sps.BatteryLifeTime / 60) : null;
                    toFullMin = sps.BatteryFullLifeTime >= 0 ? (int?)Math.Max(0, sps.BatteryFullLifeTime / 60) : null;
                    bool chargingFlag = (sps.BatteryFlag & 0x08) == 0x08;
                    bool noBattery = (sps.BatteryFlag & 0x80) == 0x80;
                    state = "unknown";
                    if (noBattery)
                    {
                        state = acOnline == true ? "ac" : "unknown";
                    }
                    else if (acOnline == true)
                    {
                        if (chargingFlag)
                            state = "charging";
                        else if (pct.HasValue && pct.Value >= 99.5)
                            state = "full";
                        else
                            state = "ac";
                    }
                    else if (acOnline == false)
                    {
                        state = "discharging";
                    }
                }
            }
            catch { /* ignore */ }

            // 2) 高级：WMI root\\WMI（逐字段容错）
            double? voltageMv = null; double? currentMa = null; double? powerW = null; int? cycle = null; string? condition = null;
            double? fullMah = null; double? designMah = null; string? manufacturer = null; string? serial = null; string? mfgDate = null; int? timeOnBatterySec = null;
            double? tempC = null;

            try
            {
                var scope = new ManagementScope(@"\\\.\root\WMI");
                scope.Connect();

                // BatteryStatus：Voltage / Current / Rate / RemainingCapacity 等（不同机型字段差异较大）
                TryQuery(scope, "SELECT * FROM BatteryStatus", mo =>
                {
                    voltageMv ??= ReadDouble(mo, new[] { "Voltage" });
                    // 优先直接电流（mA）；否则尝试 Rate/ChargeRate/DischargeRate（mW），可直接用于功率
                    currentMa ??= ReadDouble(mo, new[] { "Current", "ChargeCurrent" });
                    var rateMw = ReadDouble(mo, new[] { "Rate", "ChargeRate", "DischargeRate" });
                    if (!powerW.HasValue && rateMw.HasValue)
                    {
                        powerW = Math.Round(Math.Abs(rateMw.Value) / 1000.0, 2);
                    }
                    // 某些实现有 ElapsedTimeOnBattery 或 DischargeTime（秒）
                    timeOnBatterySec ??= (int?)ReadDouble(mo, new[] { "ElapsedTime", "ElapsedTimeOnBattery", "DischargeTime" });
                    // 尝试温度：部分实现可能直接提供摄氏度，也可能是 0.1K（与热区一致）
                    if (!tempC.HasValue)
                    {
                        var tRaw = ReadDouble(mo, new[] { "Temperature", "BatteryTemperature", "Temp" });
                        if (tRaw.HasValue)
                        {
                            // 经验换算：若数值大于 2000，按 0.1K 处理；否则若在 -20~120 范围内视为摄氏度
                            var v = tRaw.Value;
                            if (v > 2000)
                            {
                                tempC = Math.Round((v - 2732.0) / 10.0, 1);
                            }
                            else if (v > -50 && v < 200)
                            {
                                tempC = Math.Round(v, 1);
                            }
                        }
                    }
                });

                // BatteryFullChargedCapacity：FullChargedCapacity（通常 mWh）
                double? fullMwh = null;
                TryQuery(scope, "SELECT * FROM BatteryFullChargedCapacity", mo =>
                {
                    fullMwh ??= ReadDouble(mo, new[] { "FullChargedCapacity" });
                });

                // BatteryStaticData：DesignedCapacity（通常 mWh）及制造信息
                double? designMwh = null;
                TryQuery(scope, "SELECT * FROM BatteryStaticData", mo =>
                {
                    designMwh ??= ReadDouble(mo, new[] { "DesignedCapacity" });
                    manufacturer ??= ReadString(mo, new[] { "ManufacturerName", "Manufacturer" });
                    serial ??= ReadString(mo, new[] { "SerialNumber", "Serial" });
                    mfgDate ??= ReadString(mo, new[] { "ManufactureDate", "ManufacturedDate" });
                });

                // BatteryCycleCount：CycleCount
                TryQuery(scope, "SELECT * FROM BatteryCycleCount", mo =>
                {
                    cycle ??= (int?)ReadDouble(mo, new[] { "CycleCount" });
                });

                // 电流/电压→功率估算
                if (!powerW.HasValue && voltageMv.HasValue && currentMa.HasValue)
                {
                    powerW = Math.Round((voltageMv.Value / 1000.0) * (currentMa.Value / 1000.0), 2);
                }

                // mWh → mAh（需电压）
                if (voltageMv.HasValue)
                {
                    if (fullMwh.HasValue)
                        fullMah = Math.Round(fullMwh.Value * 1000.0 / Math.Max(1.0, voltageMv.Value), 0);
                    if (designMwh.HasValue)
                        designMah = Math.Round(designMwh.Value * 1000.0 / Math.Max(1.0, voltageMv.Value), 0);
                }

                // 健康状况（粗略推断）：
                // 若满充容量/设计容量可得且比值很低则提示更换
                if (fullMah.HasValue && designMah.HasValue && designMah.Value > 0)
                {
                    var ratio = fullMah.Value / designMah.Value;
                    if (ratio < 0.5) condition = "replace_now";
                    else if (ratio < 0.7) condition = "replace_soon";
                    else condition = "normal";
                }

                // 热区（ACPI）：MSAcpi_ThermalZoneTemperature（单位常见为 0.1K）
                // 尝试挑选名称包含 BAT/BATT/Battery 的热区；否则若仅有一个热区也可作为近似环境温度
                try
                {
                    TryQuery(scope, "SELECT * FROM MSAcpi_ThermalZoneTemperature", mo =>
                    {
                        try
                        {
                            var name = ReadString(mo, new[] { "InstanceName", "Name" }) ?? string.Empty;
                            var cur = ReadDouble(mo, new[] { "CurrentTemperature" });
                            if (!cur.HasValue) return;
                            var c = Math.Round((cur.Value - 2732.0) / 10.0, 1);
                            // 选择最相关的：优先包含 BAT 关键字
                            if (!tempC.HasValue)
                            {
                                if (!string.IsNullOrEmpty(name) &&
                                    (name.IndexOf("BAT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("BATT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("BATTERY", StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    tempC = c;
                                }
                                else
                                {
                                    // 若没有关键字匹配，且系统仅返回极少热区时，可作为兜底环境温度
                                    tempC ??= c;
                                }
                            }
                        }
                        catch { }
                    });
                }
                catch { }
            }
            catch { /* 忽略 WMI 读取异常 */ }

            var battery = new Dictionary<string, object?>
            {
                ["percentage"] = pct,
                ["state"] = state,
                ["time_remaining_min"] = remainMin,
                ["time_to_full_min"] = toFullMin,
                ["ac_line_online"] = acOnline,
                ["time_on_battery_sec"] = timeOnBatterySec,
                ["temperature_c"] = tempC,
                ["cycle_count"] = cycle,
                ["condition"] = condition,
                ["full_charge_capacity_mah"] = fullMah,
                ["design_capacity_mah"] = designMah,
                ["voltage_mv"] = voltageMv,
                ["current_ma"] = currentMa,
                ["power_w"] = powerW,
                ["manufacturer"] = manufacturer,
                ["serial_number"] = serial,
                ["manufacture_date"] = mfgDate,
            };

            // 3) 适配器（尽力推断）
            Dictionary<string, object?>? adapter = null;
            try
            {
                bool? present = null;
                string? chargeMode = null;
                double? aVolt = null; double? aCurr = null; double? aWatts = null; // 仅在可读时填充
                bool? isPd = null; double? ratedW = null; double? negotiatedW = null;

                // 基于 AC 状态的启发式
                if (acOnline.HasValue)
                {
                    present = acOnline.Value;
                    if (acOnline.Value)
                    {
                        if (state == "charging") chargeMode = "charging";
                        else if (state == "full") chargeMode = "full";
                        else chargeMode = "maintenance";
                    }
                    else
                    {
                        chargeMode = null;
                    }
                }

                // 若 WMI 可提供 BatteryStatus.Rate（mW），可作为整机功率（接近适配器输出）。
                if (!aWatts.HasValue && powerW.HasValue) aWatts = powerW;

                // PnP 设备启发式识别是否存在充电器/适配器（USB-PD/AC Adapter/Charger）
                bool foundPnP = false; bool foundPd = false;
                TryQueryCim("root\\CIMV2", "SELECT Name,PNPClass,DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%AC%' OR Name LIKE '%Adapter%' OR Name LIKE '%Charger%' OR Name LIKE '%Power%'", mo =>
                {
                    foundPnP = true;
                    var name = ReadString(mo, new[] { "Name" }) ?? string.Empty;
                    var dev = ReadString(mo, new[] { "DeviceID" }) ?? string.Empty;
                    if (!string.IsNullOrEmpty(dev) && dev.StartsWith("USB\\\\VID_", StringComparison.OrdinalIgnoreCase))
                    {
                        foundPd = true;
                    }
                    if (!string.IsNullOrEmpty(name) && name.IndexOf("PD", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foundPd = true;
                    }
                });
                if (present is null && foundPnP) present = true;
                if (foundPd) isPd = true;

                if (present == true || chargeMode != null || aVolt.HasValue || aCurr.HasValue || aWatts.HasValue || isPd.HasValue)
                {
                    // 计算 PD 协商档位（若可推导）
                    double? negV = aVolt.HasValue ? Math.Round(aVolt.Value / 1000.0, 2) : (double?)null; // V
                    double? negA = aCurr.HasValue ? Math.Round(aCurr.Value / 1000.0, 2) : (double?)null; // A
                    double? negW = negotiatedW.HasValue ? Math.Round(negotiatedW.Value, 2) : (double?)null; // W

                    var pd = new Dictionary<string, object?>
                    {
                        ["protocol"] = isPd == true ? "USB-PD" : null,
                        ["negotiated_profile"] = (negV.HasValue || negA.HasValue || negW.HasValue) ? new
                        {
                            voltage_v = negV,
                            current_a = negA,
                            power_w = negW
                        } : null,
                        // 预留：源能力 PDO 表（暂无法读取，留空）
                        ["caps"] = Array.Empty<object>()
                    };

                    adapter = new Dictionary<string, object?>
                    {
                        ["present"] = present,
                        ["rated_watts"] = ratedW,
                        ["negotiated_watts"] = negotiatedW,
                        ["voltage_mv"] = aVolt,
                        ["current_ma"] = aCurr,
                        ["is_pd_fast_charge"] = isPd,
                        ["charge_mode"] = chargeMode,
                        ["pd"] = pd,
                    };
                }
            }
            catch { /* ignore adapter errors */ }

            // 4) USB 设备原始列表（调试视图）
            object? usb = null;
            try
            {
                var list = new List<Dictionary<string, object?>>();
                TryQueryCim("root\\CIMV2", "SELECT Name,PNPClass,DeviceID,Status,Description FROM Win32_PnPEntity WHERE PNPClass='USB' OR DeviceID LIKE 'USB%'", mo =>
                {
                    var item = new Dictionary<string, object?>
                    {
                        ["name"] = ReadString(mo, new[] { "Name" }),
                        ["pnp_class"] = ReadString(mo, new[] { "PNPClass" }),
                        ["device_id"] = ReadString(mo, new[] { "DeviceID" }),
                        ["status"] = ReadString(mo, new[] { "Status" }),
                        ["description"] = ReadString(mo, new[] { "Description" })
                    };
                    list.Add(item);
                });
                // 额外：UCSI 控制器探测（调试）
                var ucsi = new List<Dictionary<string, object?>>();
                TryQueryCim("root\\CIMV2", "SELECT Name,DeviceID,Status,Description FROM Win32_PnPEntity", mo =>
                {
                    var name = (ReadString(mo, new[] { "Name" }) ?? string.Empty).ToLowerInvariant();
                    var desc = (ReadString(mo, new[] { "Description" }) ?? string.Empty).ToLowerInvariant();
                    if (name.Contains("ucsi") || desc.Contains("ucsi") || name.Contains("type-c") || desc.Contains("type-c") || name.Contains("type c") || desc.Contains("type c"))
                    {
                        ucsi.Add(new Dictionary<string, object?>
                        {
                            ["name"] = ReadString(mo, new[] { "Name" }),
                            ["device_id"] = ReadString(mo, new[] { "DeviceID" }),
                            ["status"] = ReadString(mo, new[] { "Status" }),
                            ["description"] = ReadString(mo, new[] { "Description" })
                        });
                    }
                });
                usb = new { devices = list.ToArray(), ucsi_controllers = ucsi.ToArray() };
            }
            catch { /* ignore usb errors */ }

            // 5) UPS（尽力采集）
            Dictionary<string, object?>? ups = null;
            try
            {
                bool anyUps = false;
                double? upsPct = null; double? upsRuntimeMin = null; string? powerSource = null;
                double? inVolt = null; double? inHz = null; double? loadPct = null;

                // 优先 Win32_UninterruptiblePowerSupply
                TryQueryCim("root\\CIMV2", "SELECT * FROM Win32_UninterruptiblePowerSupply", mo =>
                {
                    anyUps = true;
                    upsPct ??= ReadDouble(mo, new[] { "EstimatedChargeRemaining", "EstimatedBatteryChargeRemaining" });
                    upsRuntimeMin ??= ReadDouble(mo, new[] { "EstimatedRunTime", "RuntimeToEmpty" });
                    // 频率/电压/负载（若提供）
                    inVolt ??= ReadDouble(mo, new[] { "InputVoltage", "LineVoltage" });
                    inHz ??= ReadDouble(mo, new[] { "InputFrequency", "LineFrequency" });
                    loadPct ??= ReadDouble(mo, new[] { "LoadPercentage", "UPSLoad" });
                });

                // 回退：某些 UPS 以电池呈现（Win32_Battery，多实例且包含 UPS 关键词）
                if (!anyUps)
                {
                    TryQueryCim("root\\CIMV2", "SELECT * FROM Win32_Battery", mo =>
                    {
                        var name = ReadString(mo, new[] { "Name", "Caption", "Description" }) ?? string.Empty;
                        if (name.IndexOf("UPS", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            anyUps = true;
                            upsPct ??= ReadDouble(mo, new[] { "EstimatedChargeRemaining" });
                            upsRuntimeMin ??= ReadDouble(mo, new[] { "EstimatedRunTime" });
                        }
                    });
                }

                if (anyUps)
                {
                    // 电源来源：若系统 AC 在线则 ac，否则 battery/unknown
                    powerSource = acOnline == true ? "ac" : (acOnline == false ? "battery" : "unknown");
                    // 单位规范：若 RuntimeToEmpty 之类是秒，尽量转分钟（此处若>300 则视为分钟，不变；若>0 且 <300 可能是分钟本身，也不转换）
                    if (upsRuntimeMin.HasValue && upsRuntimeMin.Value > 0)
                    {
                        // 无法可靠区分单位，保持原值为分钟的假设（Win32 常用 EstimatedRunTime 为分钟）
                        upsRuntimeMin = Math.Round(upsRuntimeMin.Value, 0);
                    }

                    ups = new Dictionary<string, object?>
                    {
                        ["present"] = true,
                        ["percentage"] = upsPct,
                        ["runtime_min"] = (upsRuntimeMin.HasValue ? (int?)Convert.ToInt32(upsRuntimeMin.Value) : null),
                        ["power_source"] = powerSource,
                        ["input_voltage_v"] = inVolt,
                        ["input_frequency_hz"] = inHz,
                        ["load_percent"] = loadPct,
                    };
                }
            }
            catch { /* ignore ups errors */ }

            return new { battery, adapter = (object?)adapter, ups = (object?)ups, usb };
        }

        private static void TryQuery(ManagementScope scope, string query, Action<ManagementBaseObject> onEach)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query));
                foreach (ManagementObject mo in searcher.Get())
                {
                    try { onEach(mo); } catch { /* ignore each */ }
                }
            }
            catch { /* ignore */ }
        }

        private static void TryQueryCim(string ns, string query, Action<ManagementBaseObject> onEach)
        {
            try
            {
                var scope = new ManagementScope($"{ns}");
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query));
                foreach (ManagementObject mo in searcher.Get())
                {
                    try { onEach(mo); } catch { }
                }
            }
            catch { /* ignore */ }
        }

        private static double? ReadDouble(ManagementBaseObject mo, IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                try
                {
                    if (mo.Properties[n] != null && mo.Properties[n].Value != null)
                    {
                        var v = mo.Properties[n].Value;
                        if (v is IConvertible)
                        {
                            try { return Convert.ToDouble(v); } catch { }
                        }
                        if (double.TryParse(v.ToString(), out var d)) return d;
                    }
                }
                catch { }
            }
            return null;
        }

        private static string? ReadString(ManagementBaseObject mo, IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                try
                {
                    if (mo.Properties[n] != null && mo.Properties[n].Value != null)
                    {
                        var s = mo.Properties[n].Value.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
                catch { }
            }
            return null;
        }
    }
}
