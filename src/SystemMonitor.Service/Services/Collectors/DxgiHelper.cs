using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SystemMonitor.Service.Services.Collectors
{
    internal static class DxgiHelper
    {
        // Public API: try query DXGI budgets; key is luid string like "luid_0x{high}_0x{low}"
        public static Dictionary<string, (int localBudgetMb, int localUsageMb, int nonlocalBudgetMb, int nonlocalUsageMb)> GetAdapterBudgets()
        {
            var dict = new Dictionary<string, (int, int, int, int)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                object? factoryObj = null;
                var iidFactory1 = typeof(IDXGIFactory1).GUID;
                int hr = CreateDXGIFactory1(ref iidFactory1, out factoryObj);
                if (hr < 0 || factoryObj is not IDXGIFactory1 factory) return dict;

                try
                {
                    for (uint i = 0; ; i++)
                    {
                        IDXGIAdapter1? adapter1 = null;
                        try
                        {
                            factory.EnumAdapters1(i, out adapter1);
                        }
                        catch
                        {
                            break; // no more
                        }
                        if (adapter1 == null) break;

                        try
                        {
                            // get desc1 for LUID
                            adapter1.GetDesc1(out DXGI_ADAPTER_DESC1 desc1);
                            string luidKey = ToLuidKey(desc1.AdapterLuid);

                            // Query IDXGIAdapter3
                            var adapter3 = adapter1 as IDXGIAdapter3;
                            if (adapter3 == null)
                            {
                                // try QI
                                adapter3 = (IDXGIAdapter3?)adapter1; // RCW will QI if supported
                            }
                            if (adapter3 != null)
                            {
                                adapter3.QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, out DXGI_QUERY_VIDEO_MEMORY_INFO localInfo);
                                adapter3.QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, out DXGI_QUERY_VIDEO_MEMORY_INFO nonLocalInfo);
                                int localBudgetMb = (int)Math.Max(0, (long)(localInfo.Budget / (1024UL * 1024UL)));
                                int localUsageMb = (int)Math.Max(0, (long)(localInfo.CurrentUsage / (1024UL * 1024UL)));
                                int nonlocalBudgetMb = (int)Math.Max(0, (long)(nonLocalInfo.Budget / (1024UL * 1024UL)));
                                int nonlocalUsageMb = (int)Math.Max(0, (long)(nonLocalInfo.CurrentUsage / (1024UL * 1024UL)));
                                dict[luidKey] = (localBudgetMb, localUsageMb, nonlocalBudgetMb, nonlocalUsageMb);
                            }
                        }
                        catch
                        {
                            // ignore per-adapter errors
                        }
                        finally
                        {
                            try { if (adapter1 != null) Marshal.ReleaseComObject(adapter1); } catch { }
                        }
                    }
                }
                finally
                {
                    try { Marshal.ReleaseComObject(factory); } catch { }
                }
            }
            catch
            {
                // ignore
            }
            return dict;
        }

        internal class DxgiDescLite
        {
            public string Description { get; set; } = string.Empty;
            public uint VendorId { get; set; }
            public uint DeviceId { get; set; }
            public uint SubSysId { get; set; }
            public uint Revision { get; set; }
            public int DedicatedVideoMemoryMb { get; set; }
            public int SharedSystemMemoryMb { get; set; }
            public uint Flags { get; set; }
        }

        // Public API: query adapter desc1 per LUID key
        public static Dictionary<string, DxgiDescLite> GetAdapterDesc1()
        {
            var dict = new Dictionary<string, DxgiDescLite>(StringComparer.OrdinalIgnoreCase);
            try
            {
                object? factoryObj = null;
                var iidFactory1 = typeof(IDXGIFactory1).GUID;
                int hr = CreateDXGIFactory1(ref iidFactory1, out factoryObj);
                if (hr < 0 || factoryObj is not IDXGIFactory1 factory) return dict;

                try
                {
                    for (uint i = 0; ; i++)
                    {
                        IDXGIAdapter1? adapter1 = null;
                        try { factory.EnumAdapters1(i, out adapter1); }
                        catch { break; }
                        if (adapter1 == null) break;
                        try
                        {
                            adapter1.GetDesc1(out DXGI_ADAPTER_DESC1 d);
                            var key = ToLuidKey(d.AdapterLuid);
                            dict[key] = new DxgiDescLite
                            {
                                Description = d.Description,
                                VendorId = d.VendorId,
                                DeviceId = d.DeviceId,
                                SubSysId = d.SubSysId,
                                Revision = d.Revision,
                                DedicatedVideoMemoryMb = ToMb(d.DedicatedVideoMemory),
                                SharedSystemMemoryMb = ToMb(d.SharedSystemMemory),
                                Flags = d.Flags
                            };
                        }
                        catch { }
                        finally { try { if (adapter1 != null) Marshal.ReleaseComObject(adapter1); } catch { } }
                    }
                }
                finally { try { Marshal.ReleaseComObject(factory); } catch { } }
            }
            catch { }
            return dict;
        }

        private static int ToMb(UIntPtr v)
        {
            try { return checked((int)(v.ToUInt64() / (1024UL * 1024UL))); } catch { return 0; }
        }

        private static string ToLuidKey(LUID luid)
        {
            // match pattern like luid_0x00000000_0x0000E39A
            string hi = $"0x{(uint)luid.HighPart:X8}";
            string lo = $"0x{luid.LowPart:X8}";
            return $"luid_{hi}_{lo}";
        }

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid, out object? ppFactory);
    }

    // COM interop
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIFactory1
    {
        // we only declare the methods we need (vtable order matters)
        // Methods from IDXGIFactory
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters(uint Adapter, out IDXGIAdapter adapter);
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        // IDXGIFactory1
        void EnumAdapters1(uint Adapter, out IDXGIAdapter1 ppAdapter);
        void IsCurrent();
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc(out DXGI_ADAPTER_DESC desc);
        void CheckInterfaceSupport();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc(out DXGI_ADAPTER_DESC desc);
        void CheckInterfaceSupport();
        // IDXGIAdapter1
        void GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [ComImport, Guid("645967A4-1392-4310-A798-8053CE3E93FD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter3
    {
        // Inherit methods (we do not use them here), layout must match
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc(out DXGI_ADAPTER_DESC desc);
        void CheckInterfaceSupport();
        void GetDesc1(out DXGI_ADAPTER_DESC1 desc1);
        void GetDesc2();
        // IDXGIAdapter2 methods we skip
        void GetDesc3();
        // IDXGIAdapter3
        void RegisterHardwareContentProtectionTeardownStatusEvent();
        void UnregisterHardwareContentProtectionTeardownStatus();
        void QueryVideoMemoryInfo(uint NodeIndex, DXGI_MEMORY_SEGMENT_GROUP MemorySegmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO pVideoMemoryInfo);
        void SetVideoMemoryReservation();
        void RegisterVideoMemoryBudgetChangeNotificationEvent();
        void UnregisterVideoMemoryBudgetChangeNotification();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    internal enum DXGI_MEMORY_SEGMENT_GROUP
    {
        DXGI_MEMORY_SEGMENT_GROUP_LOCAL = 0,
        DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }
}
