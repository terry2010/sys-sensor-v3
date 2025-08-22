using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemMonitor.Service.Services
{
    // RpcServer 的基础配置与模块选择逻辑
    internal sealed partial class RpcServer
    {
        private int _baseIntervalMs = 1000;
        private int? _burstIntervalMs;
        private long _burstExpiresAt;
        private readonly Dictionary<string, int> _moduleIntervals = new(StringComparer.OrdinalIgnoreCase);

        public int GetCurrentIntervalMs(long now)
        {
            lock (_lock)
            {
                if (_burstIntervalMs.HasValue && now < _burstExpiresAt)
                {
                    return Math.Max(50, _burstIntervalMs.Value);
                }
                var interval = _baseIntervalMs;
                if (_moduleIntervals.Count > 0)
                {
                    var minMod = _moduleIntervals.Values.Min();
                    interval = Math.Min(interval, minMod);
                }
                return interval;
            }
        }

        public ISet<string> GetEnabledModules()
        {
            lock (_lock)
            {
                if (_moduleIntervals.Count == 0)
                {
                    return new HashSet<string>(new[] { "cpu", "memory" }, StringComparer.OrdinalIgnoreCase);
                }
                return new HashSet<string>(_moduleIntervals.Keys, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
