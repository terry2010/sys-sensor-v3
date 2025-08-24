using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SystemMonitor.Service.Services.Interop;
using SystemMonitor.Service.Services;

namespace SystemMonitor.Service.Services.Collectors
{
    /// <summary>
    /// 外设电量采集器（增量实现）：
    ///  - 第一步：仅枚举 BLE 设备接口（SetupAPI），不读取 GATT，输出扩展字段 interface_path 等；
    ///  - 后续：补充 BluetoothGATT* 读取 Battery Service/Level 与设备信息缓存。
    /// Name 固定为 "peripherals"，与前后端契约一致。
    /// </summary>
    internal sealed class PeripheralBatteryCollector : IMetricsCollector
    {
        public string Name => "peripherals";
        private static readonly object _lock = new();
        private static long _cacheTs;
        private static List<object>? _cacheItems;
        private static object? _cacheDebug;
        private const int CacheMs = 3_000; // 3s：缩短缓存，加快断开后的收敛

        // 每设备级缓存与统计
        private sealed class DevCache
        {
            public long FirstSeenTs;
            public long LastSeenTs;
            public long LastUpdateTs; // 成功读取电量的时间
            public int ErrorCount;
            public int LastErrorStage;
            public int LastErrorHr;
        }
        private static readonly Dictionary<string, DevCache> _perDev = new();

        public object? Collect()
        {
            try
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                List<object>? items = null;
                object? debug = null;
                lock (_lock)
                {
                    if (_cacheItems != null && now - _cacheTs < CacheMs)
                    {
                        items = _cacheItems;
                        debug = _cacheDebug;
                    }
                }
                if (items == null)
                {
                    int enumCount = 0;
                    int errorCount = 0;
                    var sample = new List<string>();
                    items = EnumerateBleInterfacesSafe((path) =>
                    {
                        enumCount++;
                        if (sample.Count < 3 && !string.IsNullOrWhiteSpace(path)) sample.Add(path);
                    }, (err) => { errorCount++; });
                    // WinRT 回退：在配置开关启用时尝试查询经典蓝牙
                    int winrtCount = 0;
                    bool winrtSupported = false;
                    var winrtNamesSample = new List<string>(3);
                    try
                    {
                        if (RpcServer.GetPeripheralsWinrtFallbackEnabledStatic())
                        {
                            winrtSupported = WinRtDeviceInfo.IsSupported();
                            var list = winrtSupported ? WinRtDeviceInfo.TryQueryBatteryPercents() : new List<(string? id, string? name, double? percent)>();
                            winrtCount = list.Count;
                            foreach (var (id, name, percent) in list)
                            {
                                if (winrtNamesSample.Count < 3 && !string.IsNullOrWhiteSpace(name)) winrtNamesSample.Add(name!);
                                // 附加一条 classic 蓝牙设备的电量数据
                                double? batt = percent.HasValue ? Math.Max(0, Math.Min(100, percent.Value)) : (double?)null;
                                var nowTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                items.Add(new
                                {
                                    id = id,
                                    name = name,
                                    kind = (string?)null,
                                    connection = "classic",
                                    battery_percent = batt,
                                    battery_state = (string?)null,
                                    source = "winrt_deviceinfo",
                                    last_update_ts = batt.HasValue ? (long?)nowTs : null,
                                    // 扩展字段：尽量对齐现有 schema
                                    interface_path = (string?)null,
                                    device_instance_id = id,
                                    ble_address = (string?)null,
                                    address = (string?)null,
                                    manufacturer = (string?)null,
                                    product = (string?)null,
                                    vendor_id = (int?)null,
                                    product_id = (int?)null,
                                    rssi = (int?)null,
                                    services = (string[]?)null,
                                    first_seen_ts = nowTs,
                                    last_seen_ts = nowTs,
                                    present = true,
                                    has_battery_service = (bool?)null,
                                    battery_source = "winrt_deviceinfo",
                                    level_valid_reason = (string?)null,
                                    sampling_ms = (long)0,
                                    retry_count = 0,
                                    // 调试字段（winrt 无 GATT 过程）
                                    open_ok = (bool?)null,
                                    gatt_service_found = (bool?)null,
                                    gatt_char_found = (bool?)null,
                                    gatt_err_stage = (int?)null,
                                    gatt_hr_services = (int?)null,
                                    gatt_hr_chars = (int?)null,
                                    gatt_hr_value = (int?)null
                                });
                            }
                        }
                    }
                    catch { /* ignore winrt errors */ }

                    debug = new { enum_count = enumCount, error = errorCount, sample_paths = sample.ToArray(), winrt_supported = winrtSupported, winrt_count = winrtCount, winrt_names_sample = winrtNamesSample.ToArray() };
                    lock (_lock)
                    {
                        _cacheItems = items;
                        _cacheTs = now;
                        _cacheDebug = debug;
                    }
                }
                return debug == null ? new { batteries = items.ToArray() } : new { batteries = items.ToArray(), debug };
            }
            catch
            {
                // 故障隔离：失败时返回空数组，不影响其他模块
                return new { batteries = Array.Empty<object>() };
            }
        }

        private static List<object> EnumerateBleInterfacesSafe(Action<string>? onEachPath = null, Action<Exception>? onError = null)
        {
            var list = new List<object>();
            IntPtr hDevInfo = IntPtr.Zero;
            try
            {
                var guid = BleGattInterop.GUID_BLUETOOTHLE_DEVICE_INTERFACE;
                hDevInfo = BleGattInterop.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, (uint)(BleGattInterop.DIGCF_DEVICEINTERFACE | BleGattInterop.DIGCF_PRESENT));
                if (hDevInfo == IntPtr.Zero || hDevInfo.ToInt64() == 0 || hDevInfo.ToInt64() == -1)
                {
                    return list;
                }
                uint index = 0;
                while (true)
                {
                    var ifData = new BleGattInterop.SP_DEVICE_INTERFACE_DATA
                    {
                        cbSize = (uint)Marshal.SizeOf<BleGattInterop.SP_DEVICE_INTERFACE_DATA>()
                    };
                    bool okEnum = BleGattInterop.SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref guid, index, ref ifData);
                    if (!okEnum)
                    {
                        break; // 到尾或失败
                    }
                    // 查询所需长度
                    int required = 0;
                    BleGattInterop.SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required <= 0 || required > 4096)
                    {
                        index++;
                        continue;
                    }
                    // 准备细节结构
                    var detail = new BleGattInterop.SP_DEVICE_INTERFACE_DETAIL_DATA();
                    detail.cbSize = (uint)(IntPtr.Size == 8 ? 8 : (4 + Marshal.SystemDefaultCharSize));
                    bool okDetail = BleGattInterop.SetupDiGetDeviceInterfaceDetail(hDevInfo, ref ifData, ref detail, required, out _, IntPtr.Zero);
                    if (!okDetail)
                    {
                        index++;
                        continue;
                    }
                    var path = detail.DevicePath;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        onEachPath?.Invoke(path);
                        // 每设备统计
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (!_perDev.TryGetValue(path, out var dc))
                        {
                            dc = new DevCache { FirstSeenTs = now, LastSeenTs = now, LastUpdateTs = 0, ErrorCount = 0, LastErrorStage = 0, LastErrorHr = 0 };
                            _perDev[path] = dc;
                        }
                        dc.LastSeenTs = now;

                        double? batt = null;
                        bool openOk = false; bool svcOk = false; bool chOk = false; int errStage = 0; int hrS = 0; int hrC = 0; int hrV = 0;
                        string[]? svcList = null;
                        int retryCount = 0;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            batt = TryReadBatteryPercent(path, out openOk, out svcOk, out chOk, out errStage, out hrS, out hrC, out hrV, out svcList, out retryCount);
                        }
                        catch (Exception ex)
                        {
                            onError?.Invoke(ex);
                        }
                        finally
                        {
                            sw.Stop();
                        }

                        if (batt is double) dc.LastUpdateTs = now;
                        if (errStage != 0)
                        {
                            dc.ErrorCount++;
                            // 记录最末次错误（优先服务/值阶段）
                            dc.LastErrorStage = errStage;
                            dc.LastErrorHr = (errStage == 2 ? hrS : errStage == 3 ? hrC : hrV);
                        }

                        // 解析 BLE 地址（从 dev_xxxxxx 取 HEX）
                        string? bleAddr = null;
                        try
                        {
                            var idx = path.IndexOf("dev_", StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                var hex = path.Substring(idx + 4, Math.Min(12, Math.Max(0, path.Length - (idx + 4))));
                                if (hex.Length >= 12)
                                {
                                    bleAddr = string.Join(":", new[] { hex[0..2], hex[2..4], hex[4..6], hex[6..8], hex[8..10], hex[10..12] });
                                }
                            }
                        }
                        catch { }

                        // has_battery_service + services UUID 列表（string）
                        string[]? services = svcList;
                        bool? hasBatteryService = svcOk ? true : (services != null ? Array.Exists(services, s => string.Equals(s, "0000180f-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase)) : (bool?)null);

                        // 来源与原因
                        string batterySource = "ble_gatt";
                        string? levelReason = null;
                        if (batt == null)
                        {
                            levelReason = errStage switch
                            {
                                1 => "open_failed",
                                2 => (svcOk ? "no_battery_service" : (hrS == 0 ? "services_unknown" : $"services_error_0x{hrS:X}")),
                                3 => (chOk ? "battery_level_missing" : (hrC == 0 ? "chars_unknown" : $"chars_error_0x{hrC:X}")),
                                4 => (hrV == 0 ? "value_unknown" : $"value_error_0x{hrV:X}"),
                                _ => "unsupported_or_sleeping"
                            };
                        }

                        list.Add(new
                        {
                            id = (string?)null,
                            name = (string?)null,
                            kind = (string?)null,
                            connection = "ble",
                            battery_percent = batt,
                            battery_state = (string?)null,
                            source = "ble_gatt",
                            last_update_ts = dc.LastUpdateTs == 0 ? (long?)null : dc.LastUpdateTs,
                            // 扩展字段
                            interface_path = path,
                            device_instance_id = (string?)null,
                            ble_address = bleAddr,
                            address = bleAddr,
                            manufacturer = (string?)null,
                            product = (string?)null,
                            vendor_id = (int?)null,
                            product_id = (int?)null,
                            rssi = (int?)null,
                            services = services,
                            first_seen_ts = dc.FirstSeenTs,
                            last_seen_ts = dc.LastSeenTs,
                            present = openOk,
                            has_battery_service = hasBatteryService,
                            battery_source = batterySource,
                            level_valid_reason = levelReason,
                            sampling_ms = (long)sw.ElapsedMilliseconds,
                            retry_count = retryCount,
                            // 调试字段（前端可在 JSON 展开查看）
                            open_ok = openOk,
                            gatt_service_found = svcOk,
                            gatt_char_found = chOk,
                            gatt_err_stage = errStage, // 0=none,1=open,2=getServices,3=getChars,4=getValue
                            gatt_hr_services = hrS,
                            gatt_hr_chars = hrC,
                            gatt_hr_value = hrV
                        });
                    }
                    index++;
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
            finally
            {
                try { if (hDevInfo != IntPtr.Zero) BleGattInterop.SetupDiDestroyDeviceInfoList(hDevInfo); } catch { }
            }
            return list;
        }

        /// <summary>
        /// 尝试通过 GATT Battery Service/Level 读取电量（0-100）。失败返回 null。
        /// </summary>
        private static double? TryReadBatteryPercent(string interfacePath,
            out bool openOk, out bool svcOk, out bool chOk, out int errStage,
            out int hrServices, out int hrChars, out int hrValue,
            out string[]? svcList, out int retryCount)
        {
            openOk = false; svcOk = false; chOk = false; errStage = 0; hrServices = 0; hrChars = 0; hrValue = 0; svcList = null; retryCount = 0;

            using var handle = BleGattInterop.CreateFile(interfacePath, BleGattInterop.GENERIC_READ,
                BleGattInterop.FILE_SHARE_READ | BleGattInterop.FILE_SHARE_WRITE, IntPtr.Zero, BleGattInterop.OPEN_EXISTING, 0, IntPtr.Zero);
            if (BleGattInterop.IsHandleInvalid(handle))
            {
                errStage = 1; // open
                return null;
            }
            openOk = true;

            // 1) 获取服务列表
            ushort svcCount;
            int hr = BleGattInterop.BluetoothGATTGetServices(handle, 0, null, out svcCount, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_CACHE);
            hrServices = hr;
            if (svcCount == 0 || (hr != BleGattInterop.S_OK && hr != BleGattInterop.ERROR_MORE_DATA))
            {
                // 重试：强制从设备读取
                hr = BleGattInterop.BluetoothGATTGetServices(handle, 0, null, out svcCount, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE);
                hrServices = hr;
                retryCount++;
                if (svcCount == 0 || (hr != BleGattInterop.S_OK && hr != BleGattInterop.ERROR_MORE_DATA)) { errStage = 2; return null; }
            }
            var svcs = new BleGattInterop.BTH_LE_GATT_SERVICE[svcCount];
            hr = BleGattInterop.BluetoothGATTGetServices(handle, svcCount, svcs, out svcCount, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_CACHE);
            hrServices = hr;
            if (hr != BleGattInterop.S_OK && hr != BleGattInterop.ERROR_MORE_DATA)
            {
                hr = BleGattInterop.BluetoothGATTGetServices(handle, svcCount, svcs, out svcCount, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE);
                hrServices = hr;
                retryCount++;
                if (hr != BleGattInterop.S_OK && hr != BleGattInterop.ERROR_MORE_DATA) { errStage = 2; return null; }
            }
            // 填充服务 UUID 列表
            try
            {
                var arr = new string[svcs.Length];
                for (int i = 0; i < svcs.Length; i++) arr[i] = svcs[i].ServiceUuid.ToString();
                svcList = arr;
            }
            catch { svcList = null; }

            // 找电池服务
            BleGattInterop.BTH_LE_GATT_SERVICE? battSvc = null;
            for (int i = 0; i < svcs.Length; i++)
            {
                if (svcs[i].ServiceUuid == BleGattInterop.UUID_BATTERY_SERVICE)
                {
                    battSvc = svcs[i]; break;
                }
            }
            if (battSvc == null) { errStage = 2; return null; }

            var svc = battSvc.Value;
            // 2) 获取特征列表
            ushort chCount;
            hr = BleGattInterop.BluetoothGATTGetCharacteristics(handle, ref svc, 0, null, out chCount, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_CACHE);
            hrChars = hr;
            if (chCount == 0 || (hr != BleGattInterop.S_OK && hr != BleGattInterop.ERROR_MORE_DATA))
            {
                hr = BleGattInterop.BluetoothGATTGetCharacteristics(handle, ref svc, 0, null, out chCount, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE);
                hrChars = hr;
                retryCount++;
                if (chCount == 0 || (hr != BleGattInterop.S_OK && hr != BleGattInterop.ERROR_MORE_DATA)) { errStage = 3; return null; }
            }
            var charsBuf = new BleGattInterop.BTH_LE_GATT_CHARACTERISTIC[chCount];
            hr = BleGattInterop.BluetoothGATTGetCharacteristics(handle, ref svc, chCount, charsBuf, out chCount, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_CACHE);
            hrChars = hr;
            if (hr != BleGattInterop.S_OK && hr != BleGattInterop.ERROR_MORE_DATA)
            {
                hr = BleGattInterop.BluetoothGATTGetCharacteristics(handle, ref svc, chCount, charsBuf, out chCount, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE);
                hrChars = hr;
                retryCount++;
                if (hr != BleGattInterop.S_OK && hr != BleGattInterop.ERROR_MORE_DATA) { errStage = 3; return null; }
            }

            BleGattInterop.BTH_LE_GATT_CHARACTERISTIC? battChar = null;
            for (int i = 0; i < charsBuf.Length; i++)
            {
                if (charsBuf[i].CharacteristicUuid == BleGattInterop.UUID_BATTERY_LEVEL)
                {
                    battChar = charsBuf[i]; break;
                }
            }
            if (battChar == null) { errStage = 3; return null; }

            var ch = battChar.Value;
            // 3) 读取特征值
            uint size = 0;
            ushort actual;
            IntPtr p = IntPtr.Zero;
            try
            {
                // 探测大小
                hr = BleGattInterop.BluetoothGATTGetCharacteristicValue(handle, ref ch, IntPtr.Zero, ref size, out actual, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_CACHE);
                hrValue = hr;
                if (hr != BleGattInterop.ERROR_MORE_DATA && hr != BleGattInterop.S_OK)
                {
                    hr = BleGattInterop.BluetoothGATTGetCharacteristicValue(handle, ref ch, IntPtr.Zero, ref size, out actual, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE);
                    hrValue = hr;
                    retryCount++;
                    if (hr != BleGattInterop.ERROR_MORE_DATA && hr != BleGattInterop.S_OK) { errStage = 4; return null; }
                }
                if (size == 0) size = 4; // 最小兜底
                p = Marshal.AllocHGlobal((int)size);
                hr = BleGattInterop.BluetoothGATTGetCharacteristicValue(handle, ref ch, p, ref size, out actual, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_CACHE);
                hrValue = hr;
                if (hr != BleGattInterop.S_OK)
                {
                    hr = BleGattInterop.BluetoothGATTGetCharacteristicValue(handle, ref ch, p, ref size, out actual, BleGattInterop.BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE);
                    hrValue = hr;
                    retryCount++;
                    if (hr != BleGattInterop.S_OK) { errStage = 4; return null; }
                }
                // 解析第一个字节为百分比
                byte value = Marshal.ReadByte(p);
                if (value <= 100) { chOk = true; svcOk = true; errStage = 0; return (double)value; }
                // 某些设备返回非标准范围，裁剪
                chOk = true; svcOk = true; errStage = 0; return Math.Max(0, Math.Min(100, (double)value));
            }
            finally
            {
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            }
        }
    }
}
