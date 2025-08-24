using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using SystemMonitor.Service;
using SystemMonitor.Service.Services.Collectors;
using static SystemMonitor.Service.Services.SystemInfo;

namespace SystemMonitor.Service.Services
{
    /// <summary>
    /// 实际提供 JSON-RPC 方法的目标类（从 RpcHostedService 抽离）。
    /// 负责：会话状态、JSON-RPC 方法、速率/突发控制、历史缓冲与持久化。
    /// </summary>
    internal sealed partial class RpcServer
    {
        private readonly ILogger _logger;
        private long _seq;
        private readonly object _lock = new();
        private readonly Guid _connId;
        // 标记该连接是否为“事件桥”：默认 false；仅当 hello(capabilities 含 metrics_stream) 后才视为桥接
        private bool _isBridge = false;
        // 全局订阅开关（跨会话共享），确保任意连接的 subscribe_metrics 立即影响事件桥推流
        private static readonly object _subLock = new();
        private static bool _s_metricsEnabled = false;
        private long _metricsPushed;
        // 简易内存历史缓冲区（环形，最多保留最近 MaxHistory 条）
        private readonly List<HistoryItem> _history = new();
        private const int MaxHistory = 10_000;
        private static readonly HashSet<string> _supportedCapabilities = new(new[] { "metrics_stream", "burst_mode", "history_query" }, StringComparer.OrdinalIgnoreCase);
        private readonly HistoryStore _store;
        private JsonRpc? _rpc;
        // 会话级最近模块缓存：用于 snapshot/metrics 之间的回填复用
        private readonly Dictionary<string, object?> _lastModules = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _moduleCacheLock = new();
        private static bool IsWarmupPlaceholder(object? v)
        {
            try
            {
                if (v == null) return false;
                var t = v.GetType();
                var prop = t.GetProperty("status");
                if (prop == null) return false;
                var s = prop.GetValue(v) as string;
                return string.Equals(s, "warming_up", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        

        public RpcServer(ILogger logger, long initialSeq, HistoryStore store, Guid connId)
        {
            _logger = logger;
            _seq = initialSeq;
            _store = store;
            _connId = connId;
        }

        public long NextSeq() => Interlocked.Increment(ref _seq);

        public long IncrementMetricsCount() => Interlocked.Increment(ref _metricsPushed);
        
        public void SetJsonRpc(JsonRpc rpc)
        {
            _rpc = rpc;
        }

        // 读取会话缓存中的模块值（若存在）
        public bool TryGetModuleFromCache(string name, out object? val)
        {
            lock (_moduleCacheLock)
            {
                return _lastModules.TryGetValue(name, out val);
            }
        }

        // 写入/更新会话缓存中的某个模块值
        public void SetModuleCache(string name, object? val)
        {
            lock (_moduleCacheLock)
            {
                if (val == null) return;
                if (string.Equals(name, "collectors_diag", StringComparison.OrdinalIgnoreCase)) return;
                if (string.Equals(name, "gpu_raw", StringComparison.OrdinalIgnoreCase)) return;
                if (IsWarmupPlaceholder(val)) return;
                _lastModules[name] = val;
            }
        }

        // 批量将 payload 中的模块写入会话缓存（跳过 ts/seq 等非模块字段）
        public void UpdateModuleCacheFromPayload(IReadOnlyDictionary<string, object?> payload)
        {
            lock (_moduleCacheLock)
            {
                foreach (var kv in payload)
                {
                    var key = kv.Key;
                    if (string.Equals(key, "ts", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "seq", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.Equals(key, "collectors_diag", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(key, "gpu_raw", StringComparison.OrdinalIgnoreCase)) continue;
                    var val = kv.Value;
                    if (val == null) continue;
                    if (IsWarmupPlaceholder(val)) continue;
                    _lastModules[key] = val;
                }
            }
        }

        

        public bool MetricsPushEnabled
        {
            get { lock (_subLock) { return _s_metricsEnabled; } }
        }

        public bool IsBridgeConnection
        {
            get { lock (_lock) { return _isBridge; } }
        }

        // 仅用于单元/集成测试，避免不同测试间的跨连接静态状态串扰
        internal static void ResetForTests()
        {
            lock (_subLock)
            {
                _s_metricsEnabled = false;
            }
        }

        

        

        

        

        

        

        

        

        

        

        

        
    }
}
