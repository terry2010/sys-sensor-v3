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

        

        public bool MetricsPushEnabled
        {
            get { lock (_subLock) { return _s_metricsEnabled; } }
        }

        public bool IsBridgeConnection
        {
            get { lock (_lock) { return _isBridge; } }
        }

        

        

        

        

        

        

        

        

        

        

        

        
    }
}
