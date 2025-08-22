using System;

namespace SystemMonitor.Service.Services
{
    // 聚合各采样器的提供者接口，便于测试替换与未来 DI
    internal interface ISamplersProvider
    {
        (int proc, int threads) SystemCountersReadProcThread();
        double[] PerCoreCountersRead();
        (int? cur, int? max) CpuFrequencyRead();
        int?[] PerCoreFrequencyRead();
        int? CpuFrequencyBusMhz();
        int? CpuFrequencyMinMhz();
        double? CpuSensorsRead();
        object TopProcSamplerRead(int topN);
        (double? ctxSw, double? syscalls, double? intr) KernelActivitySamplerRead();
        (double l1, double l5, double l15) CpuLoadAveragesUpdate(double usagePercent);
    }
}
