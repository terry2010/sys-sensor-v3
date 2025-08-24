using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SystemMonitor.Service.Services.Interop
{
    /// <summary>
    /// 最小 BLE GATT + SetupAPI 互操作封装。
    /// 仅声明必要的 GUID/常量/结构与 P/Invoke，便于后续在采集器中使用。
    /// 注意：仅支持 Windows 10+，且需要系统已安装蓝牙适配器与驱动。
    /// </summary>
    internal static class BleGattInterop
    {
        // =====================
        // 常量与宏
        // =====================
        internal const int INVALID_HANDLE_VALUE = -1;
        internal const uint GENERIC_READ = 0x80000000;
        internal const uint GENERIC_WRITE = 0x40000000;
        internal const uint FILE_SHARE_READ = 0x00000001;
        internal const uint FILE_SHARE_WRITE = 0x00000002;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        // SetupAPI flags
        internal const int DIGCF_DEFAULT = 0x00000001;
        internal const int DIGCF_PRESENT = 0x00000002;
        internal const int DIGCF_ALLCLASSES = 0x00000004;
        internal const int DIGCF_PROFILE = 0x00000008;
        internal const int DIGCF_DEVICEINTERFACE = 0x00000010;

        // GATT 属性
        internal const ushort GATT_SERVICE_GENERIC_ACCESS = 0x1800;
        internal const ushort GATT_SERVICE_BATTERY = 0x180F;
        internal const ushort GATT_CHAR_BATTERY_LEVEL = 0x2A19;

        // =====================
        // GUIDs
        // =====================
        // 设备接口 GUID：BluetoothLE 设备接口（系统定义）
        // GUID_BLUETOOTHLE_DEVICE_INTERFACE = {781AEE18-7733-4CE4-ADD0-91F41C67B592}
        internal static readonly Guid GUID_BLUETOOTHLE_DEVICE_INTERFACE = new(0x781AEE18, 0x7733, 0x4CE4, 0xAD, 0xD0, 0x91, 0xF4, 0x1C, 0x67, 0xB5, 0x92);

        // 电池服务 UUID
        internal static readonly Guid UUID_BATTERY_SERVICE = new(0x0000180F, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);
        // 电池等级特征 UUID
        internal static readonly Guid UUID_BATTERY_LEVEL = new(0x00002A19, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);

        // =====================
        // SetupAPI 结构
        // =====================
        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public uint cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        // =====================
        // GATT 结构（最小声明）
        // =====================
        [StructLayout(LayoutKind.Sequential)]
        internal struct BTH_LE_GATT_SERVICE
        {
            public ushort AttributeHandle;
            public Guid ServiceUuid;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BTH_LE_GATT_CHARACTERISTIC
        {
            public ushort AttributeHandle;
            public ushort CharacteristicValueHandle;
            public byte CharacteristicProperties;
            public Guid CharacteristicUuid;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BTH_LE_GATT_CHARACTERISTIC_VALUE
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
            public byte[] Data;
        }

        // =====================
        // P/Invoke - SetupAPI
        // =====================
        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(
            ref Guid ClassGuid,
            IntPtr Enumerator,
            IntPtr hwndParent,
            uint Flags);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr DeviceInfoSet,
            IntPtr DeviceInfoData,
            ref Guid InterfaceClassGuid,
            uint MemberIndex,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            IntPtr DeviceInterfaceDetailData,
            int DeviceInterfaceDetailDataSize,
            out int RequiredSize,
            IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            ref SP_DEVICE_INTERFACE_DETAIL_DATA DeviceInterfaceDetailData,
            int DeviceInterfaceDetailDataSize,
            out int RequiredSize,
            IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        // =====================
        // P/Invoke - Kernel32
        // =====================
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);

        // =====================
        // P/Invoke - BluetoothApis.dll (GATT)
        // =====================
        // GATT Flags（参考 BluetoothAPIs）：
        internal const uint BLUETOOTH_GATT_FLAG_NONE = 0x00000000;
        internal const uint BLUETOOTH_GATT_FLAG_CONNECTION_ENCRYPTED = 0x00000001;
        internal const uint BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_DEVICE = 0x00000002;
        internal const uint BLUETOOTH_GATT_FLAG_FORCE_READ_FROM_CACHE = 0x00000004;
        internal const uint BLUETOOTH_GATT_FLAG_SIGNED_WRITE = 0x00000008;
        internal const uint BLUETOOTH_GATT_FLAG_WRITE_WITHOUT_RESPONSE = 0x00000010;
        [DllImport("BluetoothApis.dll", SetLastError = true)]
        internal static extern int BluetoothGATTGetServices(
            SafeFileHandle hDevice,
            ushort ServicesBufferCount,
            [Out] BTH_LE_GATT_SERVICE[]? ServicesBuffer,
            out ushort ServicesBufferActual,
            uint Flags);

        [DllImport("BluetoothApis.dll", SetLastError = true)]
        internal static extern int BluetoothGATTGetCharacteristics(
            SafeFileHandle hDevice,
            ref BTH_LE_GATT_SERVICE Service,
            ushort CharacteristicsBufferCount,
            [Out] BTH_LE_GATT_CHARACTERISTIC[]? CharacteristicsBuffer,
            out ushort CharacteristicsBufferActual,
            uint Flags);

        [DllImport("BluetoothApis.dll", SetLastError = true)]
        internal static extern int BluetoothGATTGetCharacteristicValue(
            SafeFileHandle hDevice,
            ref BTH_LE_GATT_CHARACTERISTIC Characteristic,
            IntPtr CharacteristicValueData,
            ref uint CharacteristicValueDataSize,
            out ushort CharacteristicValueDataActual,
            uint Flags);

        // HRESULT/S_OK
        internal const int S_OK = 0;
        internal const int ERROR_MORE_DATA = 234;

        // 实用方法：判断句柄有效
        internal static bool IsHandleInvalid(SafeFileHandle h)
        {
            return h == null || h.IsInvalid || h.IsClosed;
        }
    }
}
