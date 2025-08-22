using System;

namespace SystemMonitor.Service.Services
{
    internal sealed class DefaultSamplersProvider : ISamplersProvider
    {
        public (int proc, int threads) SystemCountersReadProcThread() => SystemCounters.Instance.ReadProcThread();
        public double[] PerCoreCountersRead() => PerCoreCounters.Instance.Read();
        public (int? cur, int? max) CpuFrequencyRead() => CpuFrequency.Instance.Read();
        public int?[] PerCoreFrequencyRead() => PerCoreFrequency.Instance.Read();
        public int? CpuFrequencyBusMhz() => CpuFrequency.Instance.ReadBusMhz();
        public int? CpuFrequencyMinMhz() => CpuFrequency.Instance.ReadMinMhz();
        public double? CpuSensorsRead() => CpuSensors.Instance.Read();
        public object TopProcSamplerRead(int topN) => TopProcSampler.Instance.Read(topN);
        public (double? ctxSw, double? syscalls, double? intr) KernelActivitySamplerRead() => KernelActivitySampler.Instance.Read();
        public (double l1, double l5, double l15) CpuLoadAveragesUpdate(double usagePercent) => CpuLoadAverages.Instance.Update(usagePercent);
    }
}
