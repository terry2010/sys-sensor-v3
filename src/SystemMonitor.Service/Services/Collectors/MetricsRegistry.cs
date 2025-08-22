using System.Collections.Generic;
using System;

namespace SystemMonitor.Service.Services.Collectors
{
    internal static class MetricsRegistry
    {
        private static readonly object _lock = new();
        private static readonly List<IMetricsCollector> _collectors = new();

        // 静态构造函数中完成默认采集器注册，保持旧行为与顺序
        static MetricsRegistry()
        {
            Register(new CpuCollector());
            Register(new MemoryCollector());
            Register(new DiskCollector());
            Register(new NetworkCollector());
            Register(new GpuCollector());
            Register(new SensorCollector());
        }

        public static void Register(IMetricsCollector collector)
        {
            if (collector == null) throw new ArgumentNullException(nameof(collector));
            lock (_lock)
            {
                _collectors.Add(collector);
            }
        }

        public static IReadOnlyList<IMetricsCollector> Collectors
        {
            get
            {
                lock (_lock)
                {
                    return _collectors.AsReadOnly();
                }
            }
        }
    }
}
