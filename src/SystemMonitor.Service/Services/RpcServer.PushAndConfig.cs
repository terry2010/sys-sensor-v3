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
            lock (_lock)
            {
                _burstIntervalMs = p.interval_ms;
                _burstExpiresAt = now + p.ttl_ms;
            }
            _logger.LogInformation("burst_subscribe: interval={Interval}ms ttl={Ttl}ms -> expires_at={Expires}", p.interval_ms, p.ttl_ms, _burstExpiresAt);
            // 发出状态事件：burst
            EmitState("burst", null, new { interval_ms = p.interval_ms, ttl_ms = p.ttl_ms, expires_at = _burstExpiresAt });
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
                                var mem = GetMemoryInfoMb();
                                payload["memory"] = new { total = mem.total_mb, used = mem.used_mb };
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
                            var mem = GetMemoryInfoMb();
                            payload["memory"] = new { total = mem.total_mb, used = mem.used_mb };
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
            _logger.LogInformation("set_config 收到: base_interval_ms={Base}, module_intervals=[{Mods}]",
                p.base_interval_ms,
                p.module_intervals == null ? "" : string.Join(", ", p.module_intervals.Select(kv => $"{kv.Key}={kv.Value}")));

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
                lock (_lock)
                {
                    _baseIntervalMs = newBase.Value;
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
                lock (_lock)
                {
                    _moduleIntervals.Clear();
                    foreach (var kv in sanitized)
                    {
                        _moduleIntervals[kv.Key] = kv.Value;
                    }
                }
                _logger.LogInformation("set_config: module_intervals -> {Intervals}", string.Join(", ", sanitized.Select(kv => $"{kv.Key}={kv.Value}ms")));
            }

            var result = new
            {
                ok = true,
                base_interval_ms = _baseIntervalMs,
                effective_intervals = new Dictionary<string, int>(_moduleIntervals, StringComparer.OrdinalIgnoreCase)
            };
            // 在返回后短延时发送一次 metrics（仅桥接连接），避免客户端在新的观测窗口内收不到任何事件
            if (IsBridgeConnection)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(120).ConfigureAwait(false);
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
                        var mem = GetMemoryInfoMb();
                        payload["memory"] = new { total = mem.total_mb, used = mem.used_mb };
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
    }
}
