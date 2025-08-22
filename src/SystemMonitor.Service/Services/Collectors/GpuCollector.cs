namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class GpuCollector : IMetricsCollector
    {
        public string Name => "gpu";
        public object? Collect()
        {
            // 占位：未来接入 GPU 采集（NVIDIA/AMD/Intel）
            return null;
        }
    }
}
