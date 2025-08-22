using static SystemMonitor.Service.Services.SystemInfo;

namespace SystemMonitor.Service.Services.Collectors
{
    internal sealed class MemoryCollector : IMetricsCollector
    {
        public string Name => "memory";
        public object? Collect()
        {
            var m = GetMemoryDetail();
            return new
            {
                total_mb = m.TotalMb,
                used_mb = m.UsedMb,
                available_mb = m.AvailableMb,
                percent_used = m.PercentUsed,

                cached_mb = m.CachedMb,

                commit_limit_mb = m.CommitLimitMb,
                commit_used_mb = m.CommitUsedMb,
                commit_percent = m.CommitPercent,

                swap_total_mb = m.SwapTotalMb,
                swap_used_mb = m.SwapUsedMb,

                pages_in_per_sec = m.PagesInPerSec,
                pages_out_per_sec = m.PagesOutPerSec,
                page_faults_per_sec = m.PageFaultsPerSec,

                compressed_bytes_mb = m.CompressedBytesMb,
                pool_paged_mb = m.PoolPagedMb,
                pool_nonpaged_mb = m.PoolNonpagedMb,
                standby_cache_mb = m.StandbyCacheMb,
                working_set_total_mb = m.WorkingSetTotalMb,

                memory_pressure_percent = m.MemoryPressurePercent,
                memory_pressure_level = m.MemoryPressureLevel
            };
        }
    }
}
