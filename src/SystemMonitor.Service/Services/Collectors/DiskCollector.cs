namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class DiskCollector : IMetricsCollector
    {
        public string Name => "disk";
        public object? Collect()
        {
            try
            {
                return DiskCounters.Instance.Read();
            }
            catch
            {
                return new { read_bytes_per_sec = 0L, write_bytes_per_sec = 0L, queue_length = 0.0 };
            }
        }
    }
}
