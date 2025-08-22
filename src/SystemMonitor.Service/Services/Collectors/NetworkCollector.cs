namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class NetworkCollector : IMetricsCollector
    {
        public string Name => "network";
        public object? Collect()
        {
            try
            {
                return NetCounters.Instance.Read();
            }
            catch
            {
                return new { up_bytes_per_sec = 0L, down_bytes_per_sec = 0L };
            }
        }
    }
}
