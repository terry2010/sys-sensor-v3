using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Serilog;
using System.Management;

namespace SystemMonitor.Service.Services.Collectors
{
    // 优化后的GPU采集器实现
    internal sealed class OptimizedGpuCollector : IMetricsCollector
    {
        public string Name => "gpu";

        // 增加缓存时间到1000ms（原来为200ms）
        private static readonly object _lock = new();
        private static long _lastTicks;
        private static object? _lastVal;
        private const int CacheDurationMs = 1000;

        // 性能计数器池化管理
        private static class PerformanceCounterPool
        {
            private static readonly Dictionary<string, Queue<PerformanceCounter>> _pool = 
                new Dictionary<string, Queue<PerformanceCounter>>(StringComparer.OrdinalIgnoreCase);
            private static readonly object _poolLock = new object();
            private const int MaxPoolSize = 5; // 限制池大小
            
            public static PerformanceCounter GetOrCreate(string category, string counter, string instance)
            {
                var key = $"{category}|{counter}|{instance}";
                lock (_poolLock)
                {
                    if (!_pool.TryGetValue(key, out var queue))
                    {
                        queue = new Queue<PerformanceCounter>();
                        _pool[key] = queue;
                    }
                    
                    if (queue.Count > 0)
                    {
                        return queue.Dequeue();
                    }
                    
                    try
                    {
                        return new PerformanceCounter(category, counter, instance, readOnly: true);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
            
            public static void Return(PerformanceCounter counter, string category, string counterName, string instance)
            {
                if (counter == null) return;
                
                var key = $"{category}|{counterName}|{instance}";
                lock (_poolLock)
                {
                    if (!_pool.TryGetValue(key, out var queue))
                    {
                        queue = new Queue<PerformanceCounter>();
                        _pool[key] = queue;
                    }
                    
                    // 限制池大小，避免内存泄漏
                    if (queue.Count < MaxPoolSize)
                    {
                        queue.Enqueue(counter);
                    }
                    else
                    {
                        counter.Dispose();
                    }
                }
            }
        }

        // 简化版的适配器聚合类
        private sealed class AdapterAgg
        {
            public string Key { get; }
            public double Total;
            public int? DedUsedMb;
            public int? DedTotalMb;
            public int? ShaUsedMb;
            public int? ShaTotalMb;
            
            public AdapterAgg(string key) 
            { 
                Key = key; 
            }
        }

        public object? Collect()
        {
            // 检查缓存
            var now = Environment.TickCount64;
            lock (_lock)
            {
                if (_lastVal != null && now - _lastTicks <= CacheDurationMs)
                {
                    return _lastVal;
                }
            }

            try
            {
                // 简化的GPU数据采集逻辑
                var adapters = new Dictionary<string, AdapterAgg>(StringComparer.OrdinalIgnoreCase);
                
                // 使用池化的性能计数器
                var gpuEngineCat = new PerformanceCounterCategory("GPU Engine");
                var instances = gpuEngineCat.GetInstanceNames();
                
                foreach (var inst in instances)
                {
                    try
                    {
                        // 从池中获取性能计数器
                        var counter = PerformanceCounterPool.GetOrCreate("GPU Engine", "% Utilization", inst);
                        if (counter != null)
                        {
                            var key = ParseAdapterKey(inst) ?? inst;
                            var val = counter.NextValue();
                            
                            if (!adapters.TryGetValue(key, out var agg))
                            {
                                agg = new AdapterAgg(key);
                                adapters[key] = agg;
                            }
                            
                            agg.Total += val;
                            
                            // 将计数器返回池中
                            PerformanceCounterPool.Return(counter, "GPU Engine", "% Utilization", inst);
                        }
                    }
                    catch 
                    {
                        // 忽略单个实例的错误
                    }
                }
                
                // 简化的VRAM采集
                try
                {
                    var gpuMemCat = new PerformanceCounterCategory("GPU Adapter Memory");
                    var memInstances = gpuMemCat.GetInstanceNames();
                    
                    foreach (var inst in memInstances)
                    {
                        try
                        {
                            var key = ParseAdapterKey(inst) ?? inst;
                            if (!adapters.TryGetValue(key, out var agg))
                            {
                                agg = new AdapterAgg(key);
                                adapters[key] = agg;
                            }
                            
                            // 采集专用显存使用情况
                            var dedUsedCounter = PerformanceCounterPool.GetOrCreate("GPU Adapter Memory", "Dedicated Usage", inst);
                            if (dedUsedCounter != null)
                            {
                                var dedUsedBytes = dedUsedCounter.NextValue();
                                agg.DedUsedMb = (int)Math.Max(0, Math.Round(dedUsedBytes / (1024f * 1024f)));
                                PerformanceCounterPool.Return(dedUsedCounter, "GPU Adapter Memory", "Dedicated Usage", inst);
                            }
                            
                            // 采集专用显存总量
                            var dedLimitCounter = PerformanceCounterPool.GetOrCreate("GPU Adapter Memory", "Dedicated Limit", inst);
                            if (dedLimitCounter != null)
                            {
                                var dedLimitBytes = dedLimitCounter.NextValue();
                                agg.DedTotalMb = (int)Math.Max(0, Math.Round(dedLimitBytes / (1024f * 1024f)));
                                PerformanceCounterPool.Return(dedLimitCounter, "GPU Adapter Memory", "Dedicated Limit", inst);
                            }
                        }
                        catch 
                        {
                            // 忽略单个实例的错误
                        }
                    }
                }
                catch 
                {
                    // 忽略VRAM采集错误
                }
                
                // 构造返回对象
                var list = new List<object>();
                int idx = 0;
                foreach (var kv in adapters.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var key = kv.Key;
                    var a = kv.Value;
                    double usage = Math.Clamp(a.Total, 0.0, 100.0);
                    
                    list.Add(new
                    {
                        index = idx,
                        name = key,
                        usage_percent = usage,
                        vram_dedicated_used_mb = a.DedUsedMb,
                        vram_dedicated_total_mb = a.DedTotalMb,
                        vram_shared_used_mb = a.ShaUsedMb,
                        vram_shared_total_mb = a.ShaTotalMb
                    });
                    idx++;
                }
                
                var result = new
                {
                    adapters = list.Count > 0 ? list : null
                };
                
                // 更新缓存
                lock (_lock)
                {
                    _lastVal = result;
                    _lastTicks = now;
                }
                
                return result;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "OptimizedGpuCollector.Collect error");
                
                // 返回缓存值或默认值
                lock (_lock)
                {
                    return _lastVal ?? new { adapters = (object?)null };
                }
            }
        }

        private static string ParseAdapterKey(string inst)
        {
            try
            {
                // 简化的适配器键解析
                var i = inst.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                if (i >= 0)
                {
                    var end = inst.IndexOf(' ', i);
                    if (end < 0) end = inst.IndexOf('(', i);
                    if (end < 0) end = inst.Length;
                    var key = inst.Substring(i, end - i).Trim();
                    return NormalizeLuidKey(key);
                }
                return inst;
            }
            catch 
            { 
                return inst; 
            }
        }

        private static string NormalizeLuidKey(string k)
        {
            try
            {
                var s = k;
                var idx = s.LastIndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
                if (idx > 0) s = s.Substring(0, idx);
                return s;
            }
            catch 
            { 
                return k; 
            }
        }
    }
}