using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace SystemMonitor.Service.Services.Collectors
{
    // 优化后的网络采集器实现
    internal sealed class OptimizedNetworkCollector : IMetricsCollector
    {
        public string Name => "network";

        // 增加缓存时间到2000ms（原来可能更短）
        private static readonly object _lock = new();
        private static long _lastTicks;
        private static object? _lastPayload;
        private const int CacheDurationMs = 2000;

        // 简化的接口计数器类
        private sealed class SimpleIfCounters
        {
            public string Name { get; }
            public PerformanceCounter SentBytes { get; }
            public PerformanceCounter RecvBytes { get; }
            
            public SimpleIfCounters(string name, PerformanceCounter sentBytes, PerformanceCounter recvBytes)
            {
                Name = name;
                SentBytes = sentBytes;
                RecvBytes = recvBytes;
            }
        }

        private List<SimpleIfCounters> _ifs;
        private bool _initTried;

        private static bool IsValidInterface(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.ToLowerInvariant();
            if (n.Contains("loopback") || n.Contains("isatap") || n.Contains("teredo")) return false;
            return true;
        }

        private void EnsureInit()
        {
            if (_initTried) return;
            _initTried = true;
            try
            {
                var cat = new PerformanceCounterCategory("Network Interface");
                var instances = cat.GetInstanceNames();
                var valid = instances.Where(IsValidInterface).ToArray();
                var list = new List<SimpleIfCounters>();
                
                // 批量初始化计数器以提高性能
                var countersToCreate = new List<(string instance, string counterName)>();
                foreach (var inst in valid)
                {
                    countersToCreate.Add((inst, "Bytes Sent/sec"));
                    countersToCreate.Add((inst, "Bytes Received/sec"));
                }
                
                // 并行创建计数器
                var createdCounters = countersToCreate
                    .AsParallel()
                    .WithDegreeOfParallelism(Math.Min(valid.Length * 2, Environment.ProcessorCount))
                    .Select(tuple => {
                        try
                        {
                            var counter = new PerformanceCounter("Network Interface", tuple.counterName, tuple.instance, readOnly: true);
                            // 预热计数器
                            _ = counter.NextValue();
                            return (tuple.instance, tuple.counterName, counter);
                        }
                        catch
                        {
                            return (tuple.instance, tuple.counterName, (PerformanceCounter)null);
                        }
                    })
                    .ToList();
                
                // 按实例分组并创建SimpleIfCounters对象
                var groupedCounters = createdCounters
                    .Where(x => x.counter != null)
                    .GroupBy(x => x.instance)
                    .ToDictionary(g => g.Key, g => g.ToList());
                
                foreach (var inst in valid)
                {
                    if (groupedCounters.TryGetValue(inst, out var counters))
                    {
                        var sentBytes = counters.FirstOrDefault(c => c.counterName == "Bytes Sent/sec").counter;
                        var recvBytes = counters.FirstOrDefault(c => c.counterName == "Bytes Received/sec").counter;
                        
                        if (sentBytes != null && recvBytes != null)
                        {
                            list.Add(new SimpleIfCounters(inst, sentBytes, recvBytes));
                        }
                    }
                }
                
                _ifs = list;
            }
            catch
            {
                // 初始化失败时使用回退方案
                _ifs = new List<SimpleIfCounters>();
            }
        }

        public object? Collect()
        {
            // 检查缓存
            var now = Environment.TickCount64;
            lock (_lock)
            {
                if (_lastPayload != null && now - _lastTicks <= CacheDurationMs)
                {
                    return _lastPayload;
                }
            }

            try
            {
                EnsureInit();
                
                long totalRx = 0, totalTx = 0;
                var perIf = new List<object>();
                
                if (_ifs != null && _ifs.Count > 0)
                {
                    // 并行读取计数器值以提高性能
                    var counterValues = _ifs
                        .AsParallel()
                        .WithDegreeOfParallelism(Math.Min(_ifs.Count, Environment.ProcessorCount))
                        .Select(item => {
                            try
                            {
                                long rx = 0, tx = 0;
                                rx = (long)item.RecvBytes.NextValue();
                                tx = (long)item.SentBytes.NextValue();
                                return (item.Name, rx, tx, success: true);
                            }
                            catch
                            {
                                return (item.Name, 0L, 0L, success: false);
                            }
                        })
                        .Where(x => x.success)
                        .ToList();
                    
                    foreach (var (name, rx, tx, _) in counterValues)
                    {
                        totalRx += rx;
                        totalTx += tx;
                        perIf.Add(new
                        {
                            if_id = name,
                            name = name,
                            rx_bytes_per_sec = rx,
                            tx_bytes_per_sec = tx
                        });
                    }
                }
                else
                {
                    // 回退到NetworkInterface API
                    try
                    {
                        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (!IsValidInterface(ni.Name)) continue;
                            try
                            {
                                var st = ni.GetIPv4Statistics();
                                long rx = st.BytesReceived;
                                long tx = st.BytesSent;
                                
                                totalRx += rx;
                                totalTx += tx;
                                perIf.Add(new
                                {
                                    if_id = ni.Id ?? ni.Name,
                                    name = ni.Name,
                                    rx_bytes_per_sec = rx,
                                    tx_bytes_per_sec = tx
                                });
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                
                var payload = new
                {
                    io_totals = new
                    {
                        rx_bytes_per_sec = totalRx,
                        tx_bytes_per_sec = totalTx
                    },
                    per_interface_io = perIf.ToArray()
                };
                
                // 更新缓存
                lock (_lock)
                {
                    _lastPayload = payload;
                    _lastTicks = now;
                }
                
                return payload;
            }
            catch
            {
                // 发生错误时返回缓存值或默认值
                lock (_lock)
                {
                    return _lastPayload ?? new
                    {
                        io_totals = new
                        {
                            rx_bytes_per_sec = 0L,
                            tx_bytes_per_sec = 0L
                        },
                        per_interface_io = Array.Empty<object>()
                    };
                }
            }
        }
    }
}