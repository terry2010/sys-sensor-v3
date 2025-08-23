using System;
using System.Runtime.InteropServices;

namespace SystemMonitor.Service.Services.Interop
{
    // 极简 WLAN API 互操作定义：仅覆盖本项目需要的查询
    internal static class WlanApi
    {
        private const string WlanDll = "wlanapi.dll";

        [DllImport(WlanDll, SetLastError = true)]
        public static extern int WlanOpenHandle(
            uint dwClientVersion,
            IntPtr pReserved,
            out uint pdwNegotiatedVersion,
            out IntPtr phClientHandle);

        [DllImport(WlanDll, SetLastError = true)]
        public static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport(WlanDll, SetLastError = true)]
        public static extern int WlanEnumInterfaces(
            IntPtr hClientHandle,
            IntPtr pReserved,
            out IntPtr ppInterfaceList);

        [DllImport(WlanDll, SetLastError = true)]
        public static extern void WlanFreeMemory(IntPtr pMemory);

        [DllImport(WlanDll, SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern int WlanQueryInterface(
            IntPtr hClientHandle,
            ref Guid pInterfaceGuid,
            WLAN_INT_OPCODE OpCode,
            IntPtr pReserved,
            out int pdwDataSize,
            out IntPtr ppData,
            IntPtr pWlanOpcodeValueType);

        [DllImport(WlanDll, SetLastError = true)]
        public static extern int WlanGetNetworkBssList(
            IntPtr hClientHandle,
            ref Guid pInterfaceGuid,
            ref DOT11_SSID pDot11Ssid,
            DOT11_BSS_TYPE dot11BssType,
            bool bSecurityEnabled,
            IntPtr pReserved,
            out IntPtr ppWlanBssList);

        public enum WLAN_INT_OPCODE
        {
            wlan_intf_opcode_autoconf_enabled = 1,
            wlan_intf_opcode_background_scan_enabled,
            wlan_intf_opcode_media_streaming_mode,
            wlan_intf_opcode_radio_state,
            wlan_intf_opcode_bss_type,
            wlan_intf_opcode_interface_state,
            wlan_intf_opcode_current_connection,
        }

        public enum WLAN_INTERFACE_STATE
        {
            wlan_interface_state_not_ready = 0,
            wlan_interface_state_connected = 1,
            wlan_interface_state_ad_hoc_network_formed = 2,
            wlan_interface_state_disconnecting = 3,
            wlan_interface_state_disconnected = 4,
            wlan_interface_state_associating = 5,
            wlan_interface_state_discovering = 6,
            wlan_interface_state_authenticating = 7
        }

        public enum DOT11_BSS_TYPE
        {
            dot11_BSS_type_infrastructure = 1,
            dot11_BSS_type_independent = 2,
            dot11_BSS_type_any = 3
        }

        public enum DOT11_PHY_TYPE
        {
            dot11_phy_type_unknown = 0,
            dot11_phy_type_fhss = 1,
            dot11_phy_type_dsss = 2,
            dot11_phy_type_irbaseband = 3,
            dot11_phy_type_ofdm = 4,
            dot11_phy_type_hrdsss = 5,
            dot11_phy_type_erp = 6,
            dot11_phy_type_ht = 7,   // 802.11n
            dot11_phy_type_vht = 8,  // 802.11ac
            dot11_phy_type_dmg = 9,
            dot11_phy_type_he = 10,  // 802.11ax
            dot11_phy_type_eht = 11, // 802.11be（若驱动支持）
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DOT11_SSID
        {
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strInterfaceDescription;
            public WLAN_INTERFACE_STATE isState;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_INTERFACE_INFO_LIST
        {
            public int dwNumberOfItems;
            public int dwIndex;
            // 接着是 WLAN_INTERFACE_INFO[dwNumberOfItems]
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_CONNECTION_ATTRIBUTES
        {
            public WLAN_INTERFACE_STATE isState;
            public WLAN_CONNECTION_MODE wlanConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strProfileName;
            public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
            public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
        }

        public enum WLAN_CONNECTION_MODE
        {
            wlan_connection_mode_profile = 0,
            wlan_connection_mode_temporary_profile,
            wlan_connection_mode_discovery_secure,
            wlan_connection_mode_discovery_unsecure,
            wlan_connection_mode_auto,
            wlan_connection_mode_invalid
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_ASSOCIATION_ATTRIBUTES
        {
            public DOT11_SSID dot11Ssid;
            public DOT11_BSS_TYPE dot11BssType;
            public DOT11_MAC_ADDRESS dot11Bssid;
            public DOT11_PHY_TYPE dot11PhyType;
            public uint uDot11PhyIndex;
            public uint wlanSignalQuality; // 0..100
            public uint ulRxRate; // bps
            public uint ulTxRate; // bps
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DOT11_MAC_ADDRESS
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] ucDot11MacAddress;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_SECURITY_ATTRIBUTES
        {
            [MarshalAs(UnmanagedType.Bool)] public bool bSecurityEnabled;
            [MarshalAs(UnmanagedType.Bool)] public bool bOneXEnabled;
            public DOT11_AUTH_ALGORITHM dot11AuthAlgorithm;
            public DOT11_CIPHER_ALGORITHM dot11CipherAlgorithm;
        }

        public enum DOT11_AUTH_ALGORITHM
        {
            DOT11_AUTH_ALGO_80211_OPEN = 1,
            DOT11_AUTH_ALGO_80211_SHARED_KEY = 2,
            DOT11_AUTH_ALGO_WPA = 3,
            DOT11_AUTH_ALGO_WPA_PSK = 4,
            DOT11_AUTH_ALGO_WPA_NONE = 5,
            DOT11_AUTH_ALGO_RSNA = 6,
            DOT11_AUTH_ALGO_RSNA_PSK = 7,
            DOT11_AUTH_ALGO_WPA3 = 9,
            DOT11_AUTH_ALGO_WPA3_SAE = 10,
        }

        public enum DOT11_CIPHER_ALGORITHM : uint
        {
            DOT11_CIPHER_ALGO_NONE = 0x00,
            DOT11_CIPHER_ALGO_WEP40 = 0x01,
            DOT11_CIPHER_ALGO_TKIP = 0x02,
            DOT11_CIPHER_ALGO_CCMP = 0x04,
            DOT11_CIPHER_ALGO_WEP104 = 0x05,
            DOT11_CIPHER_ALGO_GCMP = 0x08,
            DOT11_CIPHER_ALGO_GCMP_256 = 0x09,
            DOT11_CIPHER_ALGO_WEP = 0x101,
            DOT11_CIPHER_ALGO_IHV_START = 0x80000000,
            DOT11_CIPHER_ALGO_IHV_END = 0xffffffff
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_BSS_LIST
        {
            public uint TotalSize;
            public uint NumberOfItems;
            // 接着是 WLAN_BSS_ENTRY[NumberOfItems]
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_BSS_ENTRY
        {
            public DOT11_SSID dot11Ssid;
            public uint phyId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] dot11Bssid;
            public DOT11_BSS_TYPE dot11BssType;
            public DOT11_PHY_TYPE dot11BssPhyType;
            public int rssi; // dBm
            public uint linkQuality; // 0..100
            public bool inRegDomain;
            public ushort beaconPeriod;
            public ulong timestamp;
            public ulong hostTimestamp;
            public ushort capabilityInformation;
            public uint chCenterFrequency; // KHz
            public WLAN_RATE_SET wlanRateSet;
            public uint ieOffset;
            public uint ieSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WLAN_RATE_SET
        {
            public uint uRateSetLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public ushort[] usRateSet;
        }

        // 辅助：从非托管列表读取结构数组
        public static WLAN_INTERFACE_INFO[] ReadInterfaces(IntPtr pList)
        {
            var list = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(pList);
            int count = list.dwNumberOfItems;
            var arr = new WLAN_INTERFACE_INFO[count];
            IntPtr pItem = pList + Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO_LIST));
            for (int i = 0; i < count; i++)
            {
                arr[i] = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(pItem)!;
                pItem += Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO));
            }
            return arr;
        }

        public static WLAN_BSS_ENTRY[] ReadBssList(IntPtr pList)
        {
            var list = Marshal.PtrToStructure<WLAN_BSS_LIST>(pList);
            int count = (int)list.NumberOfItems;
            var arr = new WLAN_BSS_ENTRY[count];
            IntPtr pItem = pList + Marshal.SizeOf(typeof(WLAN_BSS_LIST));
            for (int i = 0; i < count; i++)
            {
                arr[i] = Marshal.PtrToStructure<WLAN_BSS_ENTRY>(pItem)!;
                pItem += Marshal.SizeOf(typeof(WLAN_BSS_ENTRY));
            }
            return arr;
        }

        public struct WLAN_BSS_ENTRY_MANAGED
        {
            public WLAN_BSS_ENTRY Entry;
            public byte[] IeBytes;
        }

        public static WLAN_BSS_ENTRY_MANAGED[] ReadBssListWithIe(IntPtr pList)
        {
            var list = Marshal.PtrToStructure<WLAN_BSS_LIST>(pList);
            int count = (int)list.NumberOfItems;
            var arr = new WLAN_BSS_ENTRY_MANAGED[count];
            IntPtr pItem = pList + Marshal.SizeOf(typeof(WLAN_BSS_LIST));
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WLAN_BSS_ENTRY>(pItem)!;
                byte[] ieBytes = Array.Empty<byte>();
                try
                {
                    if (entry.ieSize > 0)
                    {
                        IntPtr iePtr = pItem + (int)entry.ieOffset;
                        ieBytes = new byte[entry.ieSize];
                        Marshal.Copy(iePtr, ieBytes, 0, (int)entry.ieSize);
                    }
                }
                catch { }
                arr[i] = new WLAN_BSS_ENTRY_MANAGED { Entry = entry, IeBytes = ieBytes };
                pItem += Marshal.SizeOf(typeof(WLAN_BSS_ENTRY));
            }
            return arr;
        }

        public static string SsidToString(DOT11_SSID ssid)
        {
            try
            {
                if (ssid.ucSSID == null) return string.Empty;
                return System.Text.Encoding.UTF8.GetString(ssid.ucSSID, 0, (int)Math.Min(ssid.uSSIDLength, (uint)ssid.ucSSID.Length));
            }
            catch { return string.Empty; }
        }

        public static string MacToString(byte[] mac)
        {
            if (mac == null || mac.Length < 6) return string.Empty;
            return string.Format("{0:x2}:{1:x2}:{2:x2}:{3:x2}:{4:x2}:{5:x2}", mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
        }
    }
}
