using System;
using static SystemMonitor.Service.Services.Win32Interop;

namespace SystemMonitor.Service.Services.Collectors
{
    /// <summary>
    /// 电源/电池采集器（最小可用版本）。
    /// 数据来源：Win32 GetSystemPowerStatus。
    /// 仅返回基础字段：battery.percentage、state、time_remaining_min、time_to_full_min、ac_line_online。
    /// 其余高级字段后续通过 WMI 等补齐。
    /// </summary>
    internal sealed class PowerCollector : IMetricsCollector
    {
        public string Name => "power";

        public object? Collect()
        {
            try
            {
                if (!GetSystemPowerStatus(out var sps))
                {
                    // 读取失败：返回空占位，避免中断
                    return new
                    {
                        battery = new
                        {
                            percentage = (double?)null,
                            state = "unknown",
                            time_remaining_min = (int?)null,
                            time_to_full_min = (int?)null,
                            ac_line_online = (bool?)null,
                            time_on_battery_sec = (int?)null
                        }
                    };
                }

                // 百分比：255 表示未知
                double? pct = sps.BatteryLifePercent == 255 ? (double?)null : sps.BatteryLifePercent;
                // AC 状态：0 离线，1 在线，255 未知
                bool? acOnline = sps.ACLineStatus == 0 ? false : sps.ACLineStatus == 1 ? true : (bool?)null;

                // 剩余/充满时间：-1 表示未知
                int? remainMin = sps.BatteryLifeTime >= 0 ? (int?)Math.Max(0, sps.BatteryLifeTime / 60) : null;
                int? toFullMin = sps.BatteryFullLifeTime >= 0 ? (int?)Math.Max(0, sps.BatteryFullLifeTime / 60) : null;

                // 充放标志：BatteryFlag 位 3(8) 表示充电
                bool chargingFlag = (sps.BatteryFlag & 0x08) == 0x08;
                bool noBattery = (sps.BatteryFlag & 0x80) == 0x80;

                // 推断 state
                string state = "unknown";
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

                return new
                {
                    battery = new
                    {
                        percentage = pct,
                        state = state,
                        time_remaining_min = remainMin,
                        time_to_full_min = toFullMin,
                        ac_line_online = acOnline,
                        time_on_battery_sec = (int?)null
                    }
                };
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
                        time_on_battery_sec = (int?)null
                    }
                };
            }
        }
    }
}
