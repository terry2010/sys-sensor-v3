using System;

namespace SystemMonitor.Service.Services.Collectors
{
    internal interface IMetricsCollector
    {
        string Name { get; }
        object? Collect();
    }
}
