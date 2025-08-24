using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
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
        public async Task<object> snapshot(SnapshotParams? p)
        {
            // 避免响应期间插入通知
            SuppressPush(200);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 规范化模块名（mem -> memory），未指定则默认 cpu/memory
            HashSet<string> want;
            bool explicitModules;
            if (p?.modules != null && p.modules.Length > 0)
            {
                want = new HashSet<string>(p.modules.Select(m => (m ?? string.Empty).Trim().ToLowerInvariant() == "mem" ? "memory" : (m ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);
                explicitModules = true;
            }
            else
            {
                want = new HashSet<string>(new[] { "cpu", "memory" }, StringComparer.OrdinalIgnoreCase);
                explicitModules = false;
            }
            var payload = new Dictionary<string, object?> { ["ts"] = ts };
            // 高频场景：仅当未显式传入 modules 时启用快速路径，避免首页 snapshot 丢失数据
            var highFreq = !explicitModules && (want.Contains("disk") || want.Count > 2);
            if (highFreq)
            {
                // 仅在请求包含 disk 时返回占位，其他模块在高频下省略以提升吞吐
                try { if (want.Contains("disk") && !payload.ContainsKey("disk")) payload["disk"] = new { status = "warming_up" }; } catch { }
                try
                {
                    // no-op for cpu/memory in highFreq
                }
                catch { /* ignore */ }
                _logger.LogDebug("snapshot (highFreq fast-path) called, modules={Modules}", p?.modules == null ? "*" : string.Join(',', p.modules));
                return payload;
            }
            // 非高频：并行执行所有命中的采集器
            var tasks = new List<Task<(string name, object? val)>>();
            var taskByName = new Dictionary<string, Task<(string name, object? val)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in MetricsRegistry.Collectors)
            {
                if (!want.Contains(c.Name)) continue;
                var t = Task.Run<(string, object?)>(() =>
                {
                    try
                    {
                        var val = c.Collect();
                        return (c.Name, val);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "snapshot collector failed (ignored): {Collector}", c.Name);
                        return (c.Name, null);
                    }
                });
                tasks.Add(t);
                taskByName[c.Name] = t;
            }
            // 第一阶段：全局预算
            // 显式请求模块时提高预算，尽量返回数据
            var budgetMs = explicitModules ? 700 : 150;
            var all = Task.WhenAll(tasks);
            var done = await Task.WhenAny(all, Task.Delay(budgetMs)).ConfigureAwait(false);
            foreach (var t in tasks)
            {
                if (t.IsCompletedSuccessfully)
                {
                    var (name, val) = t.Result;
                    if (val != null) payload[name] = val;
                }
                else if (t.IsFaulted)
                {
                    try { _ = t.Exception; } catch { }
                }
            }
            // 第二阶段：关键模块兜底等待（cpu/memory）
            var critical = explicitModules ? want.ToArray() : new[] { "cpu", "memory" };
            var extraWaitMs = explicitModules ? 600 : 250;
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + extraWaitMs;
            foreach (var key in critical)
            {
                if (!want.Contains(key)) continue;
                if (payload.ContainsKey(key)) continue;
                if (!taskByName.TryGetValue(key, out var tk)) continue;
                try
                {
                    var remain = (int)Math.Max(0, deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    if (remain == 0) break;
                    var completed = await Task.WhenAny(tk, Task.Delay(remain)).ConfigureAwait(false);
                    if (completed == tk && tk.IsCompletedSuccessfully)
                    {
                        var (name, val) = tk.Result;
                        if (val != null) payload[name] = val;
                    }
                }
                catch { }
            }
            // 兜底回退：若关键模块仍缺失，使用轻量级即时值快速填充，避免返回不完整快照
            try
            {
                if (want.Contains("cpu") && !payload.ContainsKey("cpu"))
                {
                    var usageQuick = GetCpuUsagePercent();
                    var (uPct, sPct, iPct) = GetCpuBreakdownPercent();
                    long uptimeSec = 0;
                    try { uptimeSec = (long)(Environment.TickCount64 / 1000); } catch { uptimeSec = 0; }
                    int processCount = 0, threadCount = 0;
                    try
                    {
                        var procs = Process.GetProcesses();
                        processCount = procs.Length;
                        long th = 0;
                        foreach (var p2 in procs)
                        {
                            try { th += p2.Threads?.Count ?? 0; }
                            catch { /* some processes may deny access */ }
                            finally { try { p2.Dispose(); } catch { } }
                        }
                        threadCount = (int)Math.Max(0, Math.Min(int.MaxValue, th));
                    }
                    catch { /* ignore */ }
                    payload["cpu"] = new
                    {
                        usage_percent = usageQuick,
                        user_percent = uPct,
                        system_percent = sPct,
                        idle_percent = iPct,
                        uptime_sec = uptimeSec,
                        process_count = processCount,
                        thread_count = threadCount
                    };
                }
            }
            catch { /* ignore quick cpu fallback error */ }
            try
            {
                if (want.Contains("memory") && !payload.ContainsKey("memory"))
                {
                    var m = GetMemoryDetail();
                    payload["memory"] = new
                    {
                        total_mb = m.TotalMb,
                        used_mb = m.UsedMb
                    };
                }
            }
            catch { /* ignore quick memory fallback error */ }
            _logger.LogInformation("snapshot called, modules={Modules}", p?.modules == null ? "*" : string.Join(',', p.modules));
            // 对于显式请求但仍未返回的非关键模块，填充轻量级占位，避免前端完全缺字段
            if (explicitModules)
            {
                // 先尝试用会话缓存回填，再退回 warming_up
                try
                {
                    if (want.Contains("disk") && !payload.ContainsKey("disk"))
                    {
                        if (!TryGetModuleFromCache("disk", out var cached) || cached == null) payload["disk"] = new { status = "warming_up" }; else payload["disk"] = cached;
                    }
                }
                catch { }
                try
                {
                    if (want.Contains("network") && !payload.ContainsKey("network"))
                    {
                        if (!TryGetModuleFromCache("network", out var cached) || cached == null) payload["network"] = new { status = "warming_up" }; else payload["network"] = cached;
                    }
                }
                catch { }
                try
                {
                    if (want.Contains("gpu") && !payload.ContainsKey("gpu"))
                    {
                        if (!TryGetModuleFromCache("gpu", out var cached) || cached == null) payload["gpu"] = new { status = "warming_up" }; else payload["gpu"] = cached;
                    }
                }
                catch { }
                try
                {
                    if (want.Contains("sensor") && !payload.ContainsKey("sensor"))
                    {
                        if (!TryGetModuleFromCache("sensor", out var cached) || cached == null) payload["sensor"] = new { status = "warming_up" }; else payload["sensor"] = cached;
                    }
                }
                catch { }
                try
                {
                    if (want.Contains("power") && !payload.ContainsKey("power"))
                    {
                        if (TryGetModuleFromCache("power", out var cached) && cached != null)
                        {
                            payload["power"] = cached;
                        }
                        else
                        {
                            // 轻量回退：直接读取系统电源状态，提供可用的最小电池/AC 信息
                            bool? ac = null; double? pct = null; int? remainMin = null; int? toFullMin = null;
                            try
                            {
                                if (Win32Interop.GetSystemPowerStatus(out var sps))
                                {
                                    ac = sps.ACLineStatus == 0 ? false : sps.ACLineStatus == 1 ? true : (bool?)null;
                                    pct = sps.BatteryLifePercent == 255 ? (double?)null : sps.BatteryLifePercent;
                                    remainMin = sps.BatteryLifeTime >= 0 ? (int?)Math.Max(0, sps.BatteryLifeTime / 60) : null;
                                    toFullMin = sps.BatteryFullLifeTime >= 0 ? (int?)Math.Max(0, sps.BatteryFullLifeTime / 60) : null;
                                }
                            }
                            catch { }
                            var battery = new
                            {
                                percentage = pct,
                                state = (string?)null,
                                time_remaining_min = remainMin,
                                time_to_full_min = toFullMin,
                                ac_line_online = ac,
                                time_on_battery_sec = (int?)null,
                                temperature_c = (double?)null,
                                cycle_count = (int?)null,
                                condition = (string?)null,
                                full_charge_capacity_mah = (double?)null,
                                design_capacity_mah = (double?)null,
                                voltage_mv = (double?)null,
                                current_ma = (double?)null,
                                power_w = (double?)null,
                                manufacturer = (string?)null,
                                serial_number = (string?)null,
                                manufacture_date = (string?)null,
                            };
                            payload["power"] = new { battery, adapter = (object?)null, ups = (object?)null, usb = (object?)null };
                        }
                    }
                }
                catch { }
            }
            return payload;
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
                                memory = want.Contains("memory") && last.MemTotal.HasValue && last.MemUsed.HasValue ? new { total_mb = last.MemTotal.Value, used_mb = last.MemUsed.Value } : null
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
                            memory = want.Contains("memory") && r.MemTotal.HasValue && r.MemUsed.HasValue ? new { total_mb = r.MemTotal.Value, used_mb = r.MemUsed.Value } : null
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
                                memory = want.Contains("memory") && last.mem_total.HasValue && last.mem_used.HasValue ? new { total_mb = last.mem_total.Value, used_mb = last.mem_used.Value } : null
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
                                    memory = want.Contains("memory") && last.mem_total.HasValue && last.mem_used.HasValue ? new { total_mb = last.mem_total.Value, used_mb = last.mem_used.Value } : null
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
                                memory = want.Contains("memory") && h.mem_total.HasValue && h.mem_used.HasValue ? new { total_mb = h.mem_total.Value, used_mb = h.mem_used.Value } : null
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
                    var mem = GetMemoryDetail();
                    var item = new
                    {
                        ts = now,
                        cpu = want.Contains("cpu") ? new { usage_percent = cpu } : null,
                        memory = want.Contains("memory") ? new { total_mb = mem.TotalMb, used_mb = mem.UsedMb } : null
                    } as object;
                    resultItems = new[] { item };
                }
            }
            return new { ok = true, items = resultItems };
        }
    }
}
