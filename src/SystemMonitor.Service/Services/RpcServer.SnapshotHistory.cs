using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SystemMonitor.Service.Services.Collectors;
using static SystemMonitor.Service.Services.SystemInfo;

namespace SystemMonitor.Service.Services
{
    // RpcServer 的快照与历史查询相关实现
    internal sealed partial class RpcServer
    {
        private sealed class HistoryItem
        {
            public long ts { get; set; }
            public double? cpu { get; set; }
            public long? mem_total { get; set; }
            public long? mem_used { get; set; }
        }

        public void AppendHistory(long ts, double? cpu, (long total, long used)? mem)
        {
            lock (_lock)
            {
                _history.Add(new HistoryItem
                {
                    ts = ts,
                    cpu = cpu,
                    mem_total = mem?.total,
                    mem_used = mem?.used
                });
                // 限制历史长度
                if (_history.Count > MaxHistory)
                {
                    var remove = _history.Count - MaxHistory;
                    _history.RemoveRange(0, remove);
                }
            }
        }

        public Task PersistHistoryAsync(long ts, double? cpu, (long total, long used)? mem)
            => _store.AppendAsync(ts, cpu, mem);

        /// <summary>
        /// 获取即时快照（最小实现：CPU/内存信息）。
        /// </summary>
        public Task<object> snapshot(SnapshotParams? p)
        {
            // 避免响应期间插入通知
            SuppressPush(200);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 规范化模块名（mem -> memory），未指定则默认 cpu/memory
            HashSet<string> want;
            if (p?.modules != null && p.modules.Length > 0)
            {
                want = new HashSet<string>(p.modules.Select(m => (m ?? string.Empty).Trim().ToLowerInvariant() == "mem" ? "memory" : (m ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                want = new HashSet<string>(new[] { "cpu", "memory" }, StringComparer.OrdinalIgnoreCase);
            }
            var payload = new Dictionary<string, object?> { ["ts"] = ts };
            foreach (var c in MetricsRegistry.Collectors)
            {
                if (!want.Contains(c.Name)) continue;
                try
                {
                    var val = c.Collect();
                    if (val != null) payload[c.Name] = val;
                }
                catch (Exception ex)
                {
                    // 避免单个采集器异常导致 snapshot 整体失败
                    _logger.LogDebug(ex, "snapshot collector failed (ignored): {Collector}", c.Name);
                }
            }
            _logger.LogInformation("snapshot called, modules={Modules}", p?.modules == null ? "*" : string.Join(',', p.modules));
            return Task.FromResult<object>(payload);
        }

        /// <summary>
        /// 历史查询：从内存历史缓冲区返回真实数据，支持 step_ms 聚合（按时间桶选用最后一条）。
        /// </summary>
        public async Task<object> query_history(QueryHistoryParams p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            var from = p.from_ts;
            var to = p.to_ts <= 0 || p.to_ts < from ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : p.to_ts;
            var want = new HashSet<string>(p.modules ?? new[] { "cpu", "memory" }, StringComparer.OrdinalIgnoreCase);
            object[] resultItems;
            // 1) 先尝试从 SQLite 读取（支持聚合表）
            var useAgg = !string.IsNullOrWhiteSpace(p.agg) && !string.Equals(p.agg, "raw", StringComparison.OrdinalIgnoreCase);
            var rows = useAgg ? await _store.QueryAggAsync(p.agg!, from, to).ConfigureAwait(false)
                               : await _store.QueryAsync(from, to).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                if (p.step_ms.HasValue && p.step_ms.Value > 0)
                {
                    static long BucketEnd(long t, long bucketMs) => ((t - 1) / bucketMs + 1) * bucketMs;
                    var bucketMs = useAgg
                        ? (string.Equals(p.agg, "10s", StringComparison.OrdinalIgnoreCase) ? 10_000L : 60_000L)
                        : p.step_ms.Value;
                    resultItems = rows
                        .GroupBy(r => BucketEnd(r.Ts, bucketMs))
                        .OrderBy(g => g.Key)
                        .Select(g =>
                        {
                            var last = g.Last();
                            return new
                            {
                                ts = g.Key,
                                cpu = want.Contains("cpu") && last.Cpu.HasValue ? new { usage_percent = last.Cpu.Value } : null,
                                memory = want.Contains("memory") && last.MemTotal.HasValue && last.MemUsed.HasValue ? new { total = last.MemTotal.Value, used = last.MemUsed.Value } : null
                            } as object;
                        })
                        .ToArray();
                }
                else
                {
                    resultItems = rows
                        .Select(r => (object)new
                        {
                            ts = r.Ts,
                            cpu = want.Contains("cpu") && r.Cpu.HasValue ? new { usage_percent = r.Cpu.Value } : null,
                            memory = want.Contains("memory") && r.MemTotal.HasValue && r.MemUsed.HasValue ? new { total = r.MemTotal.Value, used = r.MemUsed.Value } : null
                        })
                        .ToArray();
                }
            }
            else
            {
                // 2) 回退到内存窗口
                List<HistoryItem> slice;
                lock (_lock)
                {
                    slice = _history.Where(h => h.ts >= from && h.ts <= to).ToList();
                }
                if (p.step_ms.HasValue && p.step_ms.Value > 0)
                {
                    static long BucketEnd(long t, long bucketMs) => ((t - 1) / bucketMs + 1) * bucketMs;
                    var useAggFallback = !string.IsNullOrWhiteSpace(p.agg) && !string.Equals(p.agg, "raw", StringComparison.OrdinalIgnoreCase);
                    var bucketMs = useAggFallback
                        ? (string.Equals(p.agg, "10s", StringComparison.OrdinalIgnoreCase) ? 10_000L : 60_000L)
                        : p.step_ms.Value;
                    resultItems = slice
                        .GroupBy(h => BucketEnd(h.ts, bucketMs))
                        .OrderBy(g => g.Key)
                        .Select(g =>
                        {
                            var last = g.Last();
                            return new
                            {
                                ts = g.Key,
                                cpu = want.Contains("cpu") && last.cpu.HasValue ? new { usage_percent = last.cpu.Value } : null,
                                memory = want.Contains("memory") && last.mem_total.HasValue && last.mem_used.HasValue ? new { total = last.mem_total.Value, used = last.mem_used.Value } : null
                            } as object;
                        })
                        .ToArray();
                }
                else
                {
                    // 若请求指定了 agg（10s/1m），但 SQLite 无数据，按聚合粒度在内存中对齐到“桶结束时间”，每桶取最后一条
                    var useAggFallback = !string.IsNullOrWhiteSpace(p.agg) && !string.Equals(p.agg, "raw", StringComparison.OrdinalIgnoreCase);
                    if (useAggFallback)
                    {
                        static long BucketEnd(long t, long bucketMs) => ((t - 1) / bucketMs + 1) * bucketMs;
                        var bucketMs = string.Equals(p.agg, "10s", StringComparison.OrdinalIgnoreCase) ? 10_000L : 60_000L;
                        var buckets = slice
                            .GroupBy(h => BucketEnd(h.ts, bucketMs))
                            .OrderBy(g => g.Key)
                            .Select(g =>
                            {
                                var last = g.Last();
                                return new
                                {
                                    ts = g.Key,
                                    cpu = want.Contains("cpu") && last.cpu.HasValue ? new { usage_percent = last.cpu.Value } : null,
                                    memory = want.Contains("memory") && last.mem_total.HasValue && last.mem_used.HasValue ? new { total = last.mem_total.Value, used = last.mem_used.Value } : null
                                } as object;
                            })
                            .ToArray();
                        resultItems = buckets;
                    }
                    else
                    {
                        resultItems = slice
                            .Select(h => (object)new
                            {
                                ts = h.ts,
                                cpu = want.Contains("cpu") && h.cpu.HasValue ? new { usage_percent = h.cpu.Value } : null,
                                memory = want.Contains("memory") && h.mem_total.HasValue && h.mem_used.HasValue ? new { total = h.mem_total.Value, used = h.mem_used.Value } : null
                            })
                            .ToArray();
                    }
                }
            }
            // 回退：当窗口内没有历史数据
            // - 若未指定聚合（raw 查询），返回一条当前即时值，避免空结果；
            // - 若指定了聚合（10s/1m），返回空数组，保持对齐语义（测试亦允许为空）。
            if (resultItems.Length == 0)
            {
                var useAggFinal = !string.IsNullOrWhiteSpace(p.agg) && !string.Equals(p.agg, "raw", StringComparison.OrdinalIgnoreCase);
                if (!useAggFinal)
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var cpu = GetCpuUsagePercent();
                    var mem = GetMemoryInfoMb();
                    var item = new
                    {
                        ts = now,
                        cpu = want.Contains("cpu") ? new { usage_percent = cpu } : null,
                        memory = want.Contains("memory") ? new { total = mem.total_mb, used = mem.used_mb } : null
                    } as object;
                    resultItems = new[] { item };
                }
            }
            return new { ok = true, items = resultItems };
        }
    }
}
