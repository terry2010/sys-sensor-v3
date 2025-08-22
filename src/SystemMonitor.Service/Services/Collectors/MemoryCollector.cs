using static SystemMonitor.Service.Services.SystemInfo;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class MemoryCollector : IMetricsCollector
    {
        public string Name => "memory";
        public object? Collect()
        {
            var mem = GetMemoryInfoMb();
            return new { total = mem.total_mb, used = mem.used_mb };
        }
    }
}
