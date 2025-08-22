using System.Collections.Generic;

namespace SystemMonitor.Service.Services.Collectors
{
    internal static class MetricsRegistry
    {
        private static readonly List<IMetricsCollector> _collectors = new()
        {
            new CpuCollector(),
            new MemoryCollector(),
            new DiskCollector(),
            new NetworkCollector(),
            new GpuCollector(),
            new SensorCollector()
        };

        public static IReadOnlyList<IMetricsCollector> Collectors => _collectors;
    }
}
