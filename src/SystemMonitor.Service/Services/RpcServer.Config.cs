using System;
using System.Collections.Generic;
using System.Linq;
using SystemMonitor.Service.Services.Collectors;

namespace SystemMonitor.Service.Services
{
    // RpcServer 的基础配置与模块选择逻辑
    internal sealed partial class RpcServer
    {
        private int _baseIntervalMs = 1000;
        private int? _burstIntervalMs;
        private long _burstExpiresAt;
        private readonly Dictionary<string, int> _moduleIntervals = new(StringComparer.OrdinalIgnoreCase);
        // 新增：全局并发度（有界并发）与模块启用/同步豁免列表
        private int _maxConcurrency = 2; // 默认 2
        private HashSet<string>? _enabledModules; // null 表示默认=全部
        private HashSet<string> _syncExemptModules = new(StringComparer.OrdinalIgnoreCase) { "cpu", "memory" };

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
                if (_enabledModules != null && _enabledModules.Count > 0)
                {
                    return new HashSet<string>(_enabledModules, StringComparer.OrdinalIgnoreCase);
                }
                if (_moduleIntervals.Count > 0)
                {
                    return new HashSet<string>(_moduleIntervals.Keys, StringComparer.OrdinalIgnoreCase);
                }
                // 默认：全部注册的采集器
                return new HashSet<string>(MetricsRegistry.Collectors.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            }
        }

        public int GetMaxConcurrency()
        {
            lock (_lock)
            {
                return _maxConcurrency;
            }
        }

        public ISet<string> GetSyncExemptModules()
        {
            lock (_lock)
            {
                return new HashSet<string>(_syncExemptModules, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
