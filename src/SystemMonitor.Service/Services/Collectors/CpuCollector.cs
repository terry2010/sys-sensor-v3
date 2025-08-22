using System;
using System.Collections.Generic;
using System.Linq;
using SystemMonitor.Service.Services;
using static SystemMonitor.Service.Services.SystemInfo;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class CpuCollector : IMetricsCollector
    {
        public string Name => "cpu";
        public object? Collect()
        {
            var usage = GetCpuUsagePercent();
            var (userPct, sysPct, idlePct) = GetCpuBreakdownPercent();
            long uptimeSec = Math.Max(0, (long)(Environment.TickCount64 / 1000));
            var (proc, threads) = SystemCounters.Instance.ReadProcThread();
            var perCore = PerCoreCounters.Instance.Read();
            var (curMHzRaw, maxMHz) = CpuFrequency.Instance.Read();
            var perCoreFreq = PerCoreFrequency.Instance.Read();
            int? curMHz = curMHzRaw;
            if (perCoreFreq.Length > 0)
            {
                var vals = perCoreFreq.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                if (vals.Length > 0) curMHz = (int)Math.Round(vals.Average());
            }
            var busMhz = CpuFrequency.Instance.ReadBusMhz();
            double? multiplier = null;
            if (busMhz.HasValue && busMhz.Value > 0 && curMHz.HasValue)
            {
                multiplier = Math.Round(curMHz.Value / (double)busMhz.Value, 2);
            }
            int? minMhz = CpuFrequency.Instance.ReadMinMhz();
            var lhm = LhmSensors.Instance.Read();
            var lhmAll = LhmSensors.Instance.DumpAll();
            var pkgTempC = lhm.pkgTemp ?? CpuSensors.Instance.Read();
            var coresTempC = lhm.cores;
            var pkgPowerW = lhm.pkgPower;
            var fanRpm = lhm.fans;

            double? cpuDie = null, cpuProximity = null;
            double? pIA = null, pGT = null, pUncore = null, pDRAM = null;
            List<int?> fanMin = new();
            List<int?> fanMax = new();
            List<int?> fanTarget = new();
            List<double?> fanDuty = new();

            try
            {
                var cpuTemps = lhmAll.Where(s => s.sensor_type == "Temperature" && s.hw_type == "Cpu").ToArray();
                cpuDie = cpuTemps.FirstOrDefault(s => s.sensor_name.IndexOf("die", StringComparison.OrdinalIgnoreCase) >= 0)?.value;
                cpuProximity = cpuTemps.FirstOrDefault(s => s.sensor_name.IndexOf("proximity", StringComparison.OrdinalIgnoreCase) >= 0)?.value;

                var cpuPowers = lhmAll.Where(s => s.sensor_type == "Power" && s.hw_type == "Cpu").ToArray();
                pIA = cpuPowers.FirstOrDefault(s => s.sensor_name.IndexOf("IA", StringComparison.OrdinalIgnoreCase) >= 0 || s.sensor_name.IndexOf("cores", StringComparison.OrdinalIgnoreCase) >= 0)?.value;
                pGT = cpuPowers.FirstOrDefault(s => s.sensor_name.IndexOf("GT", StringComparison.OrdinalIgnoreCase) >= 0 || s.sensor_name.IndexOf("graphics", StringComparison.OrdinalIgnoreCase) >= 0)?.value;
                pUncore = cpuPowers.FirstOrDefault(s => s.sensor_name.IndexOf("uncore", StringComparison.OrdinalIgnoreCase) >= 0)?.value;
                pDRAM = cpuPowers.FirstOrDefault(s => s.sensor_name.IndexOf("dram", StringComparison.OrdinalIgnoreCase) >= 0 || s.sensor_name.IndexOf("memory", StringComparison.OrdinalIgnoreCase) >= 0)?.value;

                var fanAll = lhmAll.Where(s => s.hw_type == "Motherboard" || s.hw_type == "Controller" || s.sensor_type == "Fan" || s.sensor_type == "Control").ToArray();
                var fanMinItems = fanAll.Where(s => s.sensor_type == "Fan" && s.sensor_name.IndexOf("min", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                var fanMaxItems = fanAll.Where(s => s.sensor_type == "Fan" && s.sensor_name.IndexOf("max", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                var fanTargetItems = fanAll.Where(s => s.sensor_type == "Fan" && s.sensor_name.IndexOf("target", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                var fanDutyItems = fanAll.Where(s => s.sensor_type == "Control").ToList();

                foreach (var it in fanMinItems.OrderBy(x => x.sensor_name)) fanMin.Add((int?)Math.Round(it.value ?? double.NaN));
                foreach (var it in fanMaxItems.OrderBy(x => x.sensor_name)) fanMax.Add((int?)Math.Round(it.value ?? double.NaN));
                foreach (var it in fanTargetItems.OrderBy(x => x.sensor_name)) fanTarget.Add((int?)Math.Round(it.value ?? double.NaN));
                foreach (var it in fanDutyItems.OrderBy(x => x.sensor_name)) fanDuty.Add(it.value);
            }
            catch { }

            var (l1, l5, l15) = CpuLoadAverages.Instance.Update(usage);
            var top = TopProcSampler.Instance.Read(5);
            var (ctxSw, syscalls, intr) = KernelActivitySampler.Instance.Read();

            return new
            {
                usage_percent = usage,
                user_percent = userPct,
                system_percent = sysPct,
                idle_percent = idlePct,
                uptime_sec = uptimeSec,
                load_avg_1m = l1,
                load_avg_5m = l5,
                load_avg_15m = l15,
                process_count = proc,
                thread_count = threads,
                per_core = perCore,
                per_core_mhz = perCoreFreq,
                current_mhz = curMHz,
                max_mhz = maxMHz,
                min_mhz = minMhz,
                bus_mhz = busMhz,
                multiplier = multiplier,
                package_temp_c = pkgTempC,
                cores_temp_c = coresTempC,
                package_power_w = pkgPowerW,
                fan_rpm = fanRpm,
                cpu_die_temp_c = cpuDie,
                cpu_proximity_temp_c = cpuProximity,
                cpu_power_ia_w = pIA,
                cpu_power_gt_w = pGT,
                cpu_power_uncore_w = pUncore,
                cpu_power_dram_w = pDRAM,
                fan_min_rpm = fanMin.Count > 0 ? fanMin.ToArray() : null,
                fan_max_rpm = fanMax.Count > 0 ? fanMax.ToArray() : null,
                fan_target_rpm = fanTarget.Count > 0 ? fanTarget.ToArray() : null,
                fan_duty_percent = fanDuty.Count > 0 ? fanDuty.ToArray() : null,
                lhm_sensors = lhmAll,
                top_processes = top,
                context_switches_per_sec = ctxSw,
                syscalls_per_sec = syscalls,
                interrupts_per_sec = intr
            };
        }
    }
}
