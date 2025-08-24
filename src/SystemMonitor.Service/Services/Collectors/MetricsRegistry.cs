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
            // 调整顺序：网络优先于磁盘，避免磁盘重 I/O 阻塞影响网络首帧
            Register(new NetworkCollector());
            Register(new DiskCollector());
            Register(new GpuCollector());
            // 新增：电源/电池采集模块
            Register(new PowerCollector());
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
