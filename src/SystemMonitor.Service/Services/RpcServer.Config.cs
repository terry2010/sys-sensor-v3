using System;
using System.Collections.Generic;
using System.Linq;
using SystemMonitor.Service.Services.Collectors;

namespace SystemMonitor.Service.Services
{
    // RpcServer 的基础配置与模块选择逻辑
    internal sealed partial class RpcServer
    {
        // 注意：突发相关状态为连接级（按连接控制推送速率），保持为实例字段
        private int? _burstIntervalMs;
        private long _burstExpiresAt;

        // 配置改为跨连接共享（避免短连接 set_config 与 get_config 读写不同实例而丢失）
        private static readonly object s_cfgLock = new();
        private static int s_baseIntervalMs = 1000;
        private static readonly Dictionary<string, int> s_moduleIntervals = new(StringComparer.OrdinalIgnoreCase);
        // 新增：全局并发度（有界并发）与模块启用/同步豁免列表
        private static int s_maxConcurrency = 2; // 默认 2
        private static HashSet<string>? s_enabledModules; // null 表示默认=全部
        private static HashSet<string> s_syncExemptModules = new(StringComparer.OrdinalIgnoreCase) { "cpu", "memory" };

        public int GetCurrentIntervalMs(long now)
        {
            // 先读取连接级突发设置
            int? burst;
            long burstExpire;
            lock (_lock)
            {
                burst = _burstIntervalMs;
                burstExpire = _burstExpiresAt;
            }
            if (burst.HasValue && now < burstExpire)
            {
                return Math.Max(50, burst.Value);
            }
            // 再读取全局共享配置
            lock (s_cfgLock)
            {
                var interval = s_baseIntervalMs;
                if (s_moduleIntervals.Count > 0)
                {
                    var minMod = s_moduleIntervals.Values.Min();
                    interval = Math.Min(interval, minMod);
                }
                return interval;
            }
        }

        public ISet<string> GetEnabledModules()
        {
            lock (s_cfgLock)
            {
                if (s_enabledModules != null && s_enabledModules.Count > 0)
                {
                    return new HashSet<string>(s_enabledModules, StringComparer.OrdinalIgnoreCase);
                }
                if (s_moduleIntervals.Count > 0)
                {
                    return new HashSet<string>(s_moduleIntervals.Keys, StringComparer.OrdinalIgnoreCase);
                }
                // 默认：全部注册的采集器
                return new HashSet<string>(MetricsRegistry.Collectors.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            }
        }

        public int GetMaxConcurrency()
        {
            lock (s_cfgLock)
            {
                return s_maxConcurrency;
            }
        }

        public ISet<string> GetSyncExemptModules()
        {
            lock (s_cfgLock)
            {
                return new HashSet<string>(s_syncExemptModules, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
