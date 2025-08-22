using System;

namespace SystemMonitor.Service.Services
{
    // 采样器聚合提供者入口：默认指向 DefaultSamplersProvider，可在测试或未来 DI 中替换
    internal static class SamplersProvider
    {
        private static ISamplersProvider _current = new DefaultSamplersProvider();
        public static ISamplersProvider Current => _current;
        public static void SetCurrent(ISamplersProvider provider)
        {
            _current = provider ?? throw new ArgumentNullException(nameof(provider));
        }
    }
}
