using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using static SystemMonitor.Service.Services.Win32Interop;

namespace SystemMonitor.Service.Services.Collectors
{
    // 优化后的电源采集器实现
    internal sealed class OptimizedPowerCollector : IMetricsCollector
    {
        public string Name => "power";

        // 增加缓存时间到1500ms（原来为400ms）
        private static readonly object _lock = new();
        private static long _lastTs;
        private static object? _lastPayload;
        private const int CacheDurationMs = 1500;

        public object? Collect()
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                lock (_lock)
                {
                    if (_lastPayload != null && now - _lastTs <= CacheDurationMs)
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
            // 1) 基础：GetSystemPowerStatus（轻量级API，始终查询）
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

            // 2) 高级信息：只在需要时查询WMI（避免频繁查询）
            double? voltageMv = null; double? currentMa = null; double? powerW = null; int? cycle = null; string? condition = null;
            double? fullMah = null; double? designMah = null; string? manufacturer = null; string? serial = null; string? mfgDate = null; int? timeOnBatterySec = null;
            double? tempC = null;

            // 只有在电池电量较低或正在充电时才查询详细信息
            bool needDetailedInfo = (pct.HasValue && pct.Value < 90) || state == "charging";
            
            if (needDetailedInfo)
            {
                try
                {
                    var scope = new ManagementScope(@"\\\.\root\WMI");
                    scope.Connect();

                    // BatteryStatus：Voltage / Current / Rate / RemainingCapacity
                    TryQuery(scope, "SELECT * FROM BatteryStatus", mo =>
                    {
                        voltageMv ??= ReadDouble(mo, new[] { "Voltage" });
                        // 优先直接电流（mA）；否则尝试 Rate/ChargeRate/DischargeRate（mW）
                        currentMa ??= ReadDouble(mo, new[] { "Current", "ChargeCurrent" });
                        var rateMw = ReadDouble(mo, new[] { "Rate", "ChargeRate", "DischargeRate" });
                        if (!powerW.HasValue && rateMw.HasValue)
                        {
                            powerW = Math.Round(Math.Abs(rateMw.Value) / 1000.0, 2);
                        }
                        timeOnBatterySec ??= (int?)ReadDouble(mo, new[] { "ElapsedTime", "ElapsedTimeOnBattery", "DischargeTime" });
                        
                        // 温度信息（只获取一次）
                        if (!tempC.HasValue)
                        {
                            var tRaw = ReadDouble(mo, new[] { "Temperature", "BatteryTemperature", "Temp" });
                            if (tRaw.HasValue)
                            {
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

                    // 只在需要时查询电池容量信息
                    if (pct.HasValue && pct.Value < 95)
                    {
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

                        // 健康状况（粗略推断）
                        if (fullMah.HasValue && designMah.HasValue && designMah.Value > 0)
                        {
                            var ratio = fullMah.Value / designMah.Value;
                            if (ratio < 0.5) condition = "replace_now";
                            else if (ratio < 0.7) condition = "replace_soon";
                            else condition = "normal";
                        }
                    }
                }
                catch 
                { 
                    /* 忽略 WMI 读取异常 */ 
                }
            }

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

            // 简化的适配器信息
            Dictionary<string, object?>? adapter = null;
            if (acOnline.HasValue)
            {
                adapter = new Dictionary<string, object?>
                {
                    ["present"] = acOnline.Value,
                    ["charge_mode"] = state == "charging" ? "charging" : (state == "full" ? "full" : "maintenance")
                };
            }

            return new { battery, adapter = (object?)adapter, ups = (object?)null };
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