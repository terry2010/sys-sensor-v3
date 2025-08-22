using System;

namespace SystemMonitor.Service.Services
{
    // 硬件传感器提供者入口：默认指向 LhmSensors.Instance，可在测试或未来 DI 中替换
    internal static class SensorsProvider
    {
        private static ISensorsProvider _current = LhmSensors.Instance;
        public static ISensorsProvider Current
        {
            get => _current;
        }
        public static void SetCurrent(ISensorsProvider provider)
        {
            _current = provider ?? throw new ArgumentNullException(nameof(provider));
        }
    }
}
