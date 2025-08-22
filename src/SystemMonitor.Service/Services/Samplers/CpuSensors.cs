using System;

namespace SystemMonitor.Service.Services
{
    internal sealed class CpuSensors
    {
        private static readonly Lazy<CpuSensors> _inst = new(() => new CpuSensors());
        public static CpuSensors Instance => _inst.Value;
        private long _lastTicks;
        private double? _lastPackageTempC;

        public double? Read()
        {
            var now = Environment.TickCount64;
            if (now - _lastTicks < 2_000) return _lastPackageTempC;
            double? pkg = null;
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("ROOT\\WMI", "SELECT CurrentTemperature, InstanceName FROM MSAcpi_ThermalZoneTemperature");
                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    try
                    {
                        var tenthKelvin = Convert.ToInt64(obj["CurrentTemperature"]);
                        if (tenthKelvin > 0)
                        {
                            var c = (tenthKelvin / 10.0) - 273.15;
                            if (!double.IsNaN(c) && c > -50 && c < 150)
                                pkg = Math.Max(pkg ?? double.MinValue, c);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            _lastPackageTempC = pkg; _lastTicks = now; return _lastPackageTempC;
        }
    }
}
