using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace SystemMonitor.Service.Services
{
    internal sealed class LhmSensorDto
    {
        public string? hw_type { get; set; }
        public string? hw_name { get; set; }
        public string? sensor_type { get; set; }
        public string? sensor_name { get; set; }
        public double? value { get; set; }
    }

    // LibreHardwareMonitor 采集器（CPU 包温/核心温/包功率，主板风扇）
    internal sealed class LhmSensors : ISensorsProvider
    {
        private static readonly Lazy<LhmSensors> _inst = new(() => new LhmSensors());
        public static LhmSensors Instance => _inst.Value;

        private Computer? _comp;
        private long _lastTicks;
        private (double? pkgTemp, double?[]? cores, double? pkgPower, int?[]? fans) _last;
        private bool _dumped;

        private void EnsureInit()
        {
            if (_comp != null) return;
            try
            {
                _comp = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsControllerEnabled = true,
                    IsMemoryEnabled = true,
                    IsStorageEnabled = true,
                    IsNetworkEnabled = true,
                    IsBatteryEnabled = true,
                    IsPsuEnabled = true
                };
                _comp.Open();
            }
            catch { _comp = null; }
        }

        public (double? pkgTemp, double?[]? cores, double? pkgPower, int?[]? fans) Read()
        {
            var now = Environment.TickCount64;
            if (now - _lastTicks < 1500) return _last;
            EnsureInit();
            if (_comp == null) return _last;

            try
            {
                foreach (var hw in _comp.Hardware)
                {
                    try { hw.Update(); } catch { }
                    foreach (var sh in hw.SubHardware) { try { sh.Update(); } catch { } }
                }

                // 可选：调试输出所有传感器（仅一次）
                try
                {
                    if (!_dumped && string.Equals(Environment.GetEnvironmentVariable("SYS_SENSOR_LHM_DEBUG"), "1", StringComparison.OrdinalIgnoreCase))
                    {
                        _dumped = true;
                        foreach (var hw in _comp.Hardware)
                        {
                            Serilog.Log.Information("[LHM] HW {Type} {Name}", hw.HardwareType, hw.Name);
                            foreach (var s in hw.Sensors)
                                Serilog.Log.Information("[LHM]  - {SensorType} {Name} = {Value}", s.SensorType, s.Name, s.Value);
                            foreach (var sh in hw.SubHardware)
                            {
                                Serilog.Log.Information("[LHM]  SH {Type} {Name}", sh.HardwareType, sh.Name);
                                foreach (var s in sh.Sensors)
                                    Serilog.Log.Information("[LHM]    - {SensorType} {Name} = {Value}", s.SensorType, s.Name, s.Value);
                            }
                        }
                    }
                }
                catch { }

                double? pkgT = null; List<double?> coreTs = new(); double? pkgP = null; List<int?> fans = new();

                var cpu = _comp.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu != null)
                {
                    try
                    {
                        var tempSensors = cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature).ToArray();
                        var pkgCandidates = tempSensors.Where(s => s.Name?.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0);
                        var pkgVals = pkgCandidates.Select(s => (double?)s.Value).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                        if (pkgVals.Length > 0) pkgT = pkgVals.Max();

                        var coreVals = tempSensors.Where(s => s.Name?.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0)
                            .OrderBy(s => s.Name)
                            .Select(s => (double?)s.Value).ToArray();
                        if (coreVals.Length > 0) coreTs.AddRange(coreVals);

                        var pwrSensors = cpu.Sensors.Where(s => s.SensorType == SensorType.Power && s.Name?.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0);
                        var pwrVals = pwrSensors.Select(s => (double?)s.Value).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                        if (pwrVals.Length > 0) pkgP = pwrVals.Max();
                    }
                    catch { }
                }

                // 回退：若未取到温度，遍历所有硬件/子硬件温度传感器
                try
                {
                    if (pkgT == null || coreTs.Count == 0)
                    {
                        List<ISensor> allTemp = new();
                        foreach (var hw in _comp.Hardware)
                        {
                            allTemp.AddRange(hw.Sensors.Where(s => s.SensorType == SensorType.Temperature));
                            foreach (var sh in hw.SubHardware)
                                allTemp.AddRange(sh.Sensors.Where(s => s.SensorType == SensorType.Temperature));
                        }
                        if (pkgT == null)
                        {
                            var cpuLikely = allTemp.Where(s => (s.Name?.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                                                              (s.Name?.IndexOf("Package", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                                                   .Select(s => (double?)s.Value).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                            if (cpuLikely.Length > 0) pkgT = cpuLikely.Max();
                            else
                            {
                                var any = allTemp.Select(s => (double?)s.Value).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                                if (any.Length > 0) pkgT = any.Max();
                            }
                        }
                        if (coreTs.Count == 0)
                        {
                            var coreLikely = allTemp.Where(s => (s.Name?.IndexOf("Core", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
                                                    .OrderBy(s => s.Name)
                                                    .Select(s => (double?)s.Value).ToArray();
                            if (coreLikely.Length > 0) coreTs.AddRange(coreLikely);
                        }
                    }
                }
                catch { }

                foreach (var hw in _comp.Hardware)
                {
                    try
                    {
                        foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Fan))
                        {
                            fans.Add(s.Value.HasValue ? (int?)Math.Max(0, (int)Math.Round(s.Value.Value)) : null);
                        }
                        foreach (var sh in hw.SubHardware)
                        {
                            foreach (var s in sh.Sensors.Where(s => s.SensorType == SensorType.Fan))
                            {
                                fans.Add(s.Value.HasValue ? (int?)Math.Max(0, (int)Math.Round(s.Value.Value)) : null);
                            }
                        }
                    }
                    catch { }
                }

                _last = (pkgT, coreTs.Count > 0 ? coreTs.ToArray() : null, pkgP, fans.Count > 0 ? fans.ToArray() : null);
            }
            catch { /* ignore */ }

            _lastTicks = now; return _last;
        }

        public LhmSensorDto[] DumpAll()
        {
            EnsureInit();
            if (_comp == null) return Array.Empty<LhmSensorDto>();
            try
            {
                foreach (var hw in _comp.Hardware)
                {
                    try { hw.Update(); } catch { }
                    foreach (var sh in hw.SubHardware) { try { sh.Update(); } catch { } }
                }
            }
            catch { }
            var list = new List<LhmSensorDto>();
            try
            {
                foreach (var hw in _comp.Hardware)
                {
                    foreach (var s in hw.Sensors)
                        list.Add(new LhmSensorDto { hw_type = hw.HardwareType.ToString(), hw_name = hw.Name, sensor_type = s.SensorType.ToString(), sensor_name = s.Name, value = s.Value });
                    foreach (var sh in hw.SubHardware)
                    {
                        foreach (var s in sh.Sensors)
                            list.Add(new LhmSensorDto { hw_type = sh.HardwareType.ToString(), hw_name = sh.Name, sensor_type = s.SensorType.ToString(), sensor_name = s.Name, value = s.Value });
                    }
                }
            }
            catch { }
            return list.ToArray();
        }
    }
}
