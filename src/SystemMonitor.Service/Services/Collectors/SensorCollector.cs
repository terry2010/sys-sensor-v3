namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class SensorCollector : IMetricsCollector
    {
        public string Name => "sensor";
        public object? Collect()
        {
            // 占位：未来接入更丰富的硬件传感器（温度/电压/风扇等）的聚合输出
            return null;
        }
    }
}
