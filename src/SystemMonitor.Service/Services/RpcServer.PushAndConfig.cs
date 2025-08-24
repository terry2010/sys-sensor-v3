using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using static SystemMonitor.Service.Services.SystemInfo;

namespace SystemMonitor.Service.Services
{
    // RpcServer 的突发/订阅/配置相关实现
    internal sealed partial class RpcServer
    {
        /// <summary>
        /// 临时高频订阅：在 ttl_ms 内将推流间隔降为 interval_ms。
        /// </summary>
        public Task<object> burst_subscribe(BurstParams p)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (p == null || p.interval_ms <= 0 || p.ttl_ms <= 0)
            {
                throw new InvalidOperationException("invalid_params: interval_ms>0 && ttl_ms>0 required");
            }

            // 注意：burst_subscribe 仅处理突发订阅，不处理配置项
            lock (_lock)
            {
                _burstIntervalMs = p.interval_ms;
                _burstExpiresAt = now + p.ttl_ms;
            }
            _logger.LogInformation("burst_subscribe: interval={Interval}ms ttl={Ttl}ms -> expires_at={Expires}", p.interval_ms, p.ttl_ms, _burstExpiresAt);
            // 延迟发出状态事件：避免与当前 RPC 响应交叉，导致前端响应解码报错
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(120).ConfigureAwait(false);
                    await WaitForUnsuppressedAsync(400).ConfigureAwait(false);
                    EmitState("burst", null, new { interval_ms = p.interval_ms, ttl_ms = p.ttl_ms, expires_at = _burstExpiresAt });
                }
                catch { /* ignore */ }
            });
            // 保障：在 TTL 内启动一个轻量定时器（仅桥接连接），按请求间隔发送最小 metrics，防止主循环受偶发抖动影响计数
            if (IsBridgeConnection)
            _ = Task.Run(async () =>
            {
                try
                {
                    var end = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Max(100, p.ttl_ms);
                    var step = Math.Max(50, p.interval_ms);
                    while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < end)
                    {
                        try
                        {
                            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            var payload = new System.Collections.Generic.Dictionary<string, object?>
                            {
                                ["ts"] = ts,
                                ["seq"] = NextSeq()
                            };
                            // 补充最小 CPU/内存字段
                            try
                            {
                                var cpu = GetCpuUsagePercent();
                                payload["cpu"] = new { usage_percent = cpu };
                            }
                            catch { }
                            try
                            {
                                var mem = GetMemoryDetail();
                                payload["memory"] = new { total_mb = mem.TotalMb, used_mb = mem.UsedMb };
                            }
                            catch { }
                            await _rpc!.NotifyAsync("metrics", payload).ConfigureAwait(false);
                            IncrementMetricsCount();
                            _logger.LogInformation("burst tick metrics sent: ts={Ts} interval={Interval} conn={ConnId}", ts, step, _connId);
                        }
                        catch { /* ignore single tick error */ }
                        await Task.Delay(step).ConfigureAwait(false);
                    }
                }
                catch { /* ignore ticker error */ }
            });
            return Task.FromResult<object>(new { ok = true, expires_at = _burstExpiresAt });
        }

        /// <summary>
        /// 开启/关闭 metrics 推送（默认关闭，避免与短连接响应混流）。
        /// </summary>
        public Task<object> subscribe_metrics(SubscribeMetricsParams p)
        {
            // 避免响应期间插入通知
            SuppressPush(200);
            bool enabled;
            lock (_subLock)
            {
                _s_metricsEnabled = p != null && p.enable;
                enabled = _s_metricsEnabled;
            }
            _logger.LogInformation("subscribe_metrics: enable={Enable} conn={ConnId}", enabled, _connId);
            // 订阅开启后，短延时发送一次 metrics（仅桥接连接），确保客户端在后续观测窗口内可见
            if (enabled && IsBridgeConnection)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(300).ConfigureAwait(false);
                        // 避免与 subscribe_metrics 响应交叉
                        await WaitForUnsuppressedAsync(400).ConfigureAwait(false);
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var payload = new System.Collections.Generic.Dictionary<string, object?>
                        {
                            ["ts"] = now,
                            ["seq"] = NextSeq()
                        };
                        // 补充最小 CPU/内存字段
                        try
                        {
                            var cpu = GetCpuUsagePercent();
                            payload["cpu"] = new { usage_percent = cpu };
                        }
                        catch { }
                        try
                        {
                            var mem = GetMemoryDetail();
                            payload["memory"] = new { total_mb = mem.TotalMb, used_mb = mem.UsedMb };
                        }
                        catch { }
                        await _rpc!.NotifyAsync("metrics", payload).ConfigureAwait(false);
                        IncrementMetricsCount();
                        _logger.LogInformation("prewarm metrics sent after subscribe_metrics: ts={Ts} conn={ConnId}", now, _connId);
                    }
                    catch { /* ignore */ }
                });
            }
            return Task.FromResult<object>(new { ok = true, enabled });
        }

        /// <summary>
        /// 设置配置（占位）。
        /// </summary>
        public Task<object> set_config(SetConfigParams p)
        {
            // 避免响应期间插入通知
            SuppressPush(200);
            if (p == null)
            {
                throw new InvalidOperationException("invalid_params: missing body");
            }
            _logger.LogInformation("set_config 收到: base_interval_ms={Base}, max_concurrency={Conc}, enabled_modules=[{Enabled}], sync_exempt_modules=[{SyncExempt}], module_intervals=[{Mods}], peripherals_winrt_fallback_enabled={WinRT}",
                p.base_interval_ms,
                p.max_concurrency,
                p.enabled_modules == null ? "null" : string.Join(", ", p.enabled_modules ?? Array.Empty<string>()),
                p.sync_exempt_modules == null ? "null" : string.Join(", ", p.sync_exempt_modules ?? Array.Empty<string>()),
                p.module_intervals == null ? "" : string.Join(", ", p.module_intervals.Select(kv => $"{kv.Key}={kv.Value}")),
                p.peripherals_winrt_fallback_enabled);

            int? newBase = null;
            if (p.base_interval_ms.HasValue)
            {
                var v = p.base_interval_ms.Value;
                if (v <= 0)
                {
                    throw new InvalidOperationException("invalid_params: base_interval_ms must be positive");
                }
                // 基础保护：至少 100ms，避免过低导致 CPU 抢占
                newBase = Math.Max(100, v);
            }

            if (newBase.HasValue)
            {
                lock (s_cfgLock)
                {
                    s_baseIntervalMs = newBase.Value;
                }
                _logger.LogInformation("set_config: base_interval_ms -> {Base}ms", newBase.Value);
            }

            if (p.module_intervals != null && p.module_intervals.Count > 0)
            {
                var sanitized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in p.module_intervals)
                {
                    var name = (kv.Key ?? string.Empty).Trim();
                    var val = kv.Value;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (val <= 0) throw new InvalidOperationException($"invalid_params: module_intervals[{name}] must be positive");
                    sanitized[name] = Math.Max(100, val);
                }
                lock (s_cfgLock)
                {
                    s_moduleIntervals.Clear();
                    foreach (var kv in sanitized)
                    {
                        s_moduleIntervals[kv.Key] = kv.Value;
                    }
                }
                _logger.LogInformation("set_config: module_intervals -> {Intervals}", string.Join(", ", sanitized.Select(kv => $"{kv.Key}={kv.Value}ms")));
            }

            // 新增：max_concurrency
            if (p.max_concurrency.HasValue)
            {
                var c = Math.Clamp(p.max_concurrency.Value, 1, 8);
                lock (s_cfgLock)
                {
                    s_maxConcurrency = c;
                }
                _logger.LogInformation("set_config: max_concurrency -> {C}", c);
            }

            // 新增：enabled_modules（null 表示不修改；空数组表示全部）
            if (p.enabled_modules != null)
            {
                var req = p.enabled_modules;
                HashSet<string>? newSet = null;
                if (req.Length == 0)
                {
                    newSet = null; // 表示全部
                }
                else
                {
                    var all = new HashSet<string>(Collectors.MetricsRegistry.Collectors.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
                    var filtered = req.Where(m => !string.IsNullOrWhiteSpace(m) && all.Contains(m)).Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    newSet = filtered.Count == 0 ? null : filtered;
                }
                lock (s_cfgLock)
                {
                    s_enabledModules = newSet;
                }
                _logger.LogInformation("set_config: enabled_modules -> {Mods}", newSet == null ? "ALL" : string.Join(", ", newSet));
            }

            // 新增：sync_exempt_modules
            if (p.sync_exempt_modules != null)
            {
                var all = new HashSet<string>(Collectors.MetricsRegistry.Collectors.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
                var filtered = p.sync_exempt_modules.Where(m => !string.IsNullOrWhiteSpace(m) && all.Contains(m))
                    .Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (filtered.Count == 0)
                {
                    filtered = new HashSet<string>(new[] { "cpu", "memory" }, StringComparer.OrdinalIgnoreCase);
                }
                lock (s_cfgLock)
                {
                    s_syncExemptModules = filtered;
                }
                _logger.LogInformation("set_config: sync_exempt_modules -> {Mods}", string.Join(", ", filtered));
            }

            // 新增：外设电量 WinRT 回退开关
            if (p.peripherals_winrt_fallback_enabled.HasValue)
            {
                var en = p.peripherals_winrt_fallback_enabled.Value;
                lock (s_cfgLock)
                {
                    s_peripheralsWinrtFallbackEnabled = en;
                }
                _logger.LogInformation("set_config: peripherals_winrt_fallback_enabled -> {En}", en);
            }

            // 新增：磁盘 SMART/NVMe 细粒度 TTL 与原生 SMART 开关（可选）
            try
            {
                int? smartTtl = p.disk_smart_ttl_ms;
                int? nvmeErrTtl = p.disk_nvme_errorlog_ttl_ms;
                int? nvmeIdentTtl = p.disk_nvme_ident_ttl_ms;
                bool? smartNativeEnabled = p.disk_smart_native_enabled;
                if (smartTtl.HasValue || nvmeErrTtl.HasValue || nvmeIdentTtl.HasValue || smartNativeEnabled.HasValue)
                {
                    SystemMonitor.Service.Services.Collectors.DiskCollector.ApplyConfig(smartTtl, nvmeErrTtl, nvmeIdentTtl, smartNativeEnabled);
                    _logger.LogInformation("set_config: disk ttl/native updated smart_ttl={Smart} nvme_err_ttl={Err} nvme_ident_ttl={Ident} native_enabled={Native}", smartTtl, nvmeErrTtl, nvmeIdentTtl, smartNativeEnabled);
                }
            }
            catch { /* ignore disk config errors to avoid breaking overall set_config */ }

            var result = new
            {
                ok = true,
                base_interval_ms = s_baseIntervalMs,
                effective_intervals = new Dictionary<string, int>(s_moduleIntervals, StringComparer.OrdinalIgnoreCase),
                max_concurrency = s_maxConcurrency,
                enabled_modules = GetEnabledModules().ToArray(),
                sync_exempt_modules = s_syncExemptModules.ToArray(),
                peripherals_winrt_fallback_enabled = GetPeripheralsWinrtFallbackEnabled()
            };
            // 在返回后短延时发送一次 metrics（仅桥接连接），避免客户端在新的观测窗口内收不到任何事件
            if (IsBridgeConnection)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(120).ConfigureAwait(false);
                    // 避免与 set_config 响应交叉
                    await WaitForUnsuppressedAsync(400).ConfigureAwait(false);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var payload = new System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["ts"] = now,
                        ["seq"] = NextSeq()
                    };
                    // 补充最小 CPU/内存字段
                    try
                    {
                        var cpu = GetCpuUsagePercent();
                        payload["cpu"] = new { usage_percent = cpu };
                    }
                    catch { }
                    try
                    {
                        var mem = GetMemoryDetail();
                        payload["memory"] = new { total_mb = mem.TotalMb, used_mb = mem.UsedMb };
                    }
                    catch { }
                    await _rpc!.NotifyAsync("metrics", payload).ConfigureAwait(false);
                    IncrementMetricsCount();
                    _logger.LogInformation("prewarm metrics sent after set_config: ts={Ts} conn={ConnId}", now, _connId);
                }
                catch { /* ignore */ }
            });
            return Task.FromResult<object>(result);
        }

        /// <summary>
        /// 读取当前配置（只读）。
        /// </summary>
        public Task<object> get_config()
        {
            // 避免响应期间插入通知
            SuppressPush(120);
            int baseMs; int maxConc; Dictionary<string, int> mod = new(StringComparer.OrdinalIgnoreCase);
            string[] enabled; string[] syncExempt;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int curInterval;
            long burstExpire;
            lock (s_cfgLock)
            {
                baseMs = s_baseIntervalMs;
                maxConc = s_maxConcurrency;
                mod = new Dictionary<string, int>(s_moduleIntervals, StringComparer.OrdinalIgnoreCase);
                enabled = GetEnabledModules().ToArray();
                syncExempt = s_syncExemptModules.ToArray();
            }
            lock (_lock)
            {
                curInterval = GetCurrentIntervalMs(now);
                burstExpire = _burstIntervalMs.HasValue && now < _burstExpiresAt ? _burstExpiresAt : 0;
            }
            var d = SystemMonitor.Service.Services.Collectors.DiskCollector.ReadRuntimeConfig();
            var result = new
            {
                ok = true,
                base_interval_ms = baseMs,
                effective_intervals = mod,
                max_concurrency = maxConc,
                enabled_modules = enabled,
                sync_exempt_modules = syncExempt,
                current_interval_ms = curInterval,
                burst_expires_at = burstExpire,
                peripherals_winrt_fallback_enabled = GetPeripheralsWinrtFallbackEnabled(),
                // disk runtime
                disk_smart_ttl_ms = d.smartTtlMs,
                disk_nvme_errorlog_ttl_ms = d.nvmeErrlogTtlMs,
                disk_nvme_ident_ttl_ms = d.nvmeIdentTtlMs,
                disk_smart_native_override = d.smartNativeOverride,
                disk_smart_native_effective = d.smartNativeEffective,
            };
            return Task.FromResult<object>(result);
        }
    }
}
