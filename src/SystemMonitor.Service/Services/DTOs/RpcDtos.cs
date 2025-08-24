using System.Collections.Generic;

namespace SystemMonitor.Service.Services
{
    // 保留 snake_case 命名以匹配 JSON 契约
    public sealed class HelloParams
    {
        public string app_version { get; set; } = string.Empty;
        public int protocol_version { get; set; }
        public string token { get; set; } = string.Empty;
        public string[]? capabilities { get; set; }
    }

    public sealed class SnapshotParams
    {
        public string[]? modules { get; set; }
    }

    public sealed class BurstParams
    {
        public string[]? modules { get; set; }
        public int interval_ms { get; set; }
        public int ttl_ms { get; set; }
    }

    public sealed class SetConfigParams
    {
        public int? base_interval_ms { get; set; }
        public Dictionary<string, int>? module_intervals { get; set; }
        public int? max_concurrency { get; set; }
        public string[]? enabled_modules { get; set; }
        public string[]? sync_exempt_modules { get; set; }
        public bool? persist { get; set; }
        // 新增：磁盘采集细粒度 TTL 与开关（可选）
        public int? disk_smart_ttl_ms { get; set; }
        public int? disk_nvme_errorlog_ttl_ms { get; set; }
        public int? disk_nvme_ident_ttl_ms { get; set; }
        public bool? disk_smart_native_enabled { get; set; }
        // 新增：外设电量 WinRT 回退开关（可选）
        public bool? peripherals_winrt_fallback_enabled { get; set; }
    }

    public sealed class StartParams
    {
        public string[]? modules { get; set; }
    }

    public sealed class QueryHistoryParams
    {
        public long from_ts { get; set; }
        public long to_ts { get; set; }
        public string[]? modules { get; set; }
        public int? step_ms { get; set; }
        public string? agg { get; set; } // 'raw' | '10s' | '1m'
    }

    public sealed class SubscribeMetricsParams
    {
        public bool enable { get; set; }
    }
}
