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
                return new
                {
                    io_totals = new
                    {
                        rx_bytes_per_sec = 0L,
                        tx_bytes_per_sec = 0L,
                        rx_packets_per_sec = (long?)null,
                        tx_packets_per_sec = (long?)null,
                        rx_errors_per_sec = (long?)null,
                        tx_errors_per_sec = (long?)null,
                        rx_drops_per_sec = (long?)null,
                        tx_drops_per_sec = (long?)null,
                    },
                    per_interface_io = Array.Empty<object>()
                };
            }
        }
    }
}
