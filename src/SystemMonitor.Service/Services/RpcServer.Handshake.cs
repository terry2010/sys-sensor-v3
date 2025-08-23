using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using static SystemMonitor.Service.Services.SystemInfo;

namespace SystemMonitor.Service.Services
{
    // RpcServer 的握手与启动/停止相关实现
    internal sealed partial class RpcServer
    {
        /// <summary>
        /// 握手认证：校验 token（MVP 先放行非空），返回会话信息。
        /// </summary>
        public Task<object> hello(HelloParams p)
        {
            // 入口日志：用于测试与诊断（关键字：hello:）
            try
            {
                var caps = p?.capabilities == null ? string.Empty : string.Join(',', p.capabilities);
                _logger.LogInformation("hello: app={App} proto={Proto} caps=[{Caps}] conn={ConnId}", p?.app_version, p?.protocol_version, caps, _connId);
            }
            catch { /* ignore logging error */ }
            if (p == null)
            {
                throw new InvalidOperationException("invalid_params: missing body");
            }
            if (string.IsNullOrWhiteSpace(p.token))
            {
                // 认证失败：未携带 token
                throw new UnauthorizedAccessException("unauthorized");
            }
            if (p.protocol_version != 1)
            {
                // 协议不支持
                _logger.LogWarning("hello validation failed: unsupported protocol_version={Proto}", p.protocol_version);
                throw new InvalidOperationException($"not_supported: protocol_version={p.protocol_version}");
            }
            if (p.capabilities != null && p.capabilities.Length > 0)
            {
                var unsupported = p.capabilities.Where(c => !_supportedCapabilities.Contains(c)).ToArray();
                if (unsupported.Length > 0)
                {
                    _logger.LogWarning("hello validation failed: unsupported capabilities={Caps}", string.Join(',', unsupported));
                    throw new InvalidOperationException($"not_supported: capabilities=[{string.Join(',', unsupported)}]");
                }
            }

            var sessionId = Guid.NewGuid().ToString();
            var result = new
            {
                server_version = "1.0.0",
                protocol_version = 1,
                capabilities = _supportedCapabilities.ToArray(),
                session_id = sessionId
            };
            // 若声明支持 metrics_stream，则将该连接标记为事件桥
            if (p.capabilities != null && p.capabilities.Any(c => string.Equals(c, "metrics_stream", StringComparison.OrdinalIgnoreCase)))
            {
                lock (_lock) { _isBridge = true; }
                // 为稳妥起见：桥接握手成功即默认开启推送（即使订阅指令尚未来得及发出）
                lock (_subLock) { _s_metricsEnabled = true; }
                _logger.LogInformation("hello ok (bridge): app={App} proto={Proto} caps=[{Caps}] session_id={SessionId} conn={ConnId}", p.app_version, p.protocol_version, p.capabilities == null ? string.Empty : string.Join(',', p.capabilities), sessionId, _connId);
                
                // 桥接连接建立后自动启动采集（默认采集 CPU/内存/磁盘/网络）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 延迟500毫秒，确保桥接连接完全建立
                        await Task.Delay(500);
                        await start(new StartParams { modules = new[] { "cpu", "mem", "disk", "network" } });
                        _logger.LogInformation("自动启动采集模块成功");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "自动启动采集模块失败");
                    }
                });

                // 预热：立即发送一次轻量 metrics 通知，避免初期观测窗口内为 0
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        // 避免与 hello 响应交叉
                        await WaitForUnsuppressedAsync(400).ConfigureAwait(false);
                        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var payload = new System.Collections.Generic.Dictionary<string, object?>
                        {
                            ["ts"] = now,
                            ["seq"] = NextSeq()
                        };
                        // 补充最小 CPU/内存字段，避免客户端拿到仅 ts/seq 的轻量负载
                        try
                        {
                            var cpu = GetCpuUsagePercent();
                            payload["cpu"] = new { usage_percent = cpu };
                        }
                        catch { /* ignore cpu read error */ }
                        try
                        {
                            var mem = GetMemoryInfoMb();
                            payload["memory"] = new { total_mb = mem.total_mb, used_mb = mem.used_mb };
                        }
                        catch { /* ignore mem read error */ }
                        await _rpc!.NotifyAsync("metrics", payload).ConfigureAwait(false);
                        IncrementMetricsCount();
                        _logger.LogInformation("prewarm metrics sent after hello: ts={Ts} conn={ConnId}", now, _connId);
                    }
                    catch { /* ignore */ }
                });
            }
            else
            {
                _logger.LogInformation("hello ok: app={App} proto={Proto} caps=[{Caps}] session_id={SessionId} conn={ConnId}", p.app_version, p.protocol_version, p.capabilities == null ? string.Empty : string.Join(',', p.capabilities), sessionId, _connId);
            }
            return Task.FromResult<object>(result);
        }

        /// <summary>
        /// 启动采集模块。
        /// </summary>
        public Task<object> start(StartParams? p)
        {
            // 避免响应期间插入通知
            SuppressPush(200);
            var modules = p?.modules ?? new[] { "cpu", "mem" };
            // 将外部传入的模块名规范化到内部命名（mem -> memory）并写入实例模块配置
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in modules)
            {
                if (string.IsNullOrWhiteSpace(m)) continue;
                var name = m.Trim().ToLowerInvariant() == "mem" ? "memory" : m.Trim();
                int baseMs;
                lock (s_cfgLock) { baseMs = s_baseIntervalMs; }
                map[name] = Math.Max(100, baseMs);
                enabled.Add(name);
            }
            lock (s_cfgLock)
            {
                s_moduleIntervals.Clear();
                foreach (var kv in map)
                {
                    s_moduleIntervals[kv.Key] = kv.Value;
                }
                // start 显式指定模块：更新启用集合，仅包含传入模块
                s_enabledModules = enabled.Count == 0 ? null : enabled;
            }
            _logger.LogInformation("start called, modules={modules}", string.Join(",", modules));
            // 先返回响应，避免在同一请求通道上先收到通知导致客户端解码失败
            var response = new { ok = true, started_modules = modules } as object;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    EmitState("start", null, new { modules });
                }
                catch { /* ignore */ }
            });
            return Task.FromResult<object>(response);
        }

        /// <summary>
        /// 停止采集模块。
        /// </summary>
        public Task<object> stop()
        {
            // 清空模块设置，推送循环将依据空集合回退到默认模块或停止
            lock (s_cfgLock)
            {
                s_moduleIntervals.Clear();
                // stop 后重置启用集合为全部（null 表示全部）
                s_enabledModules = null;
            }
            _logger.LogInformation("stop called, metrics collection stopped");
            // 发出状态事件：stop
            EmitState("stop");
            return Task.FromResult<object>(new { ok = true });
        }
    }
}
