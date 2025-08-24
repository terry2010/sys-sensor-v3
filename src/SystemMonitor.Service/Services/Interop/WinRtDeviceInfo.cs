using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;

namespace SystemMonitor.Service.Services.Interop
{
    internal static class WinRtDeviceInfo
    {
        // 运行时检测，避免在不支持 WinRT 的环境抛异常
        public static bool IsSupported()
        {
            try { return ApiInformation.IsTypePresent("Windows.Devices.Enumeration.DeviceInformation"); }
            catch { return false; }
        }

        /// <summary>
        /// 查询 AssociationEndpoint 设备的 System.Devices.BatteryLifePercent。
        /// 仅返回存在该属性的设备（经典蓝牙设备常见）。
        /// </summary>
        public static List<(string? id, string? name, double? percent)> TryQueryBatteryPercents(int timeoutMs = 1500)
        {
            var result = new List<(string? id, string? name, double? percent)>();
            if (!IsSupported()) return result;
            try
            {
                var props = new[]
                {
                    "System.Devices.BatteryLifePercent",
                    "System.ItemNameDisplay"
                };
                // 优先：仅查询已配对的蓝牙 AssociationEndpoint（ProtocolId 为蓝牙 GUID）
                var btProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}"; // Bluetooth GUID
                var aqs = $"System.Devices.Aep.ProtocolId:=\"{btProtocol}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
                IReadOnlyList<DeviceInformation>? coll = null;
                try
                {
                    var task = DeviceInformation.FindAllAsync(
                        aqsFilter: aqs,
                        additionalProperties: props,
                        kind: DeviceInformationKind.AssociationEndpoint
                    ).AsTask();
                    if (task.Wait(timeoutMs)) coll = task.Result;
                }
                catch { /* fall back */ }

                // 回退：无过滤 AssociationEndpoint
                if (coll == null || coll.Count == 0)
                {
                    try
                    {
                        var task2 = DeviceInformation.FindAllAsync(
                            aqsFilter: null,
                            additionalProperties: props,
                            kind: DeviceInformationKind.AssociationEndpoint
                        ).AsTask();
                        if (task2.Wait(timeoutMs)) coll = task2.Result;
                    }
                    catch { }
                }
                if (coll == null) return result;
                foreach (var di in coll)
                {
                    double? percent = null;
                    try
                    {
                        if (di.Properties != null && di.Properties.TryGetValue("System.Devices.BatteryLifePercent", out var obj) && obj != null)
                        {
                            switch (obj)
                            {
                                case double d: percent = d; break;
                                case float f: percent = (double)f; break;
                                case int i: percent = (double)i; break;
                                case uint ui: percent = (double)ui; break;
                                case long l: percent = (double)l; break;
                                case string s when double.TryParse(s, out var dv): percent = dv; break;
                                default: break;
                            }
                            // 部分系统可能返回 0-1 范围，将其放大为百分比
                            if (percent.HasValue && percent.Value > 0 && percent.Value <= 1.0)
                                percent = percent.Value * 100.0;
                        }
                    }
                    catch { /* ignore per-item parse errors */ }

                    // 只返回有电量值的设备
                    if (percent.HasValue)
                    {
                        string? name = null;
                        try
                        {
                            if (di.Properties != null && di.Properties.TryGetValue("System.ItemNameDisplay", out var objName) && objName != null)
                            {
                                name = objName.ToString();
                            }
                        }
                        catch { }
                        result.Add((di.Id, name ?? di.Name, percent));
                    }
                }
            }
            catch
            {
                // 忽略 WinRT 层异常，外部回退为空
            }
            return result;
        }
    }
}
