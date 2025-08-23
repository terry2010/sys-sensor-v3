using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemMonitor.Service.Services.Collectors
{
    // 轻量滑窗直方图聚合：按 IOPS 权重聚合读/写延迟，支持 p50/p95/p99
    // 设计：
    // - 固定对数刻度桶（0.05ms .. 5000ms），覆盖常见磁盘延迟范围
    // - 时间滑窗按秒分片（Frame），仅保留 windowSeconds 内的数据（默认 60s）
    // - Update(key, readLatMs, writeLatMs, readIops, writeIops) 在当前秒将权重加到对应桶
    // - GetPercentiles(key) 聚合窗口内所有分片，输出读/写各 p50/p95/p99
    internal sealed class LatencyAggregator
    {
        private static readonly Lazy<LatencyAggregator> _inst = new(() => new LatencyAggregator());
        public static LatencyAggregator Instance => _inst.Value;

        private readonly object _lock = new();
        private readonly Dictionary<string, Timeline> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly double[] _bucketEdges; // 桶边界（上界，单位 ms）
        private readonly int _windowSeconds;

        private LatencyAggregator(int windowSeconds = 60)
        {
            _windowSeconds = Math.Clamp(windowSeconds, 10, 600);
            // 生成对数桶：从 0.05ms 到 5000ms，步进以 10^(1/8)（约 1.333x）为比率
            var edges = new List<double>();
            double v = 0.05; // 0.05 ms
            while (v <= 5000)
            {
                edges.Add(v);
                v *= Math.Pow(10.0, 1.0 / 8.0);
            }
            // 额外一个大尾部
            edges.Add(10000);
            _bucketEdges = edges.ToArray();
        }

        private sealed class Frame
        {
            public long Second; // Unix秒
            public double[] Read;  // 权重计数（IOPS）
            public double[] Write; // 权重计数（IOPS）
            public Frame(int bucketCount)
            {
                Read = new double[bucketCount];
                Write = new double[bucketCount];
            }
        }

        private sealed class Timeline
        {
            public readonly int BucketCount;
            public readonly LinkedList<Frame> Frames = new();
            public Timeline(int bucketCount) { BucketCount = bucketCount; }
        }

        private int FindBucket(double ms)
        {
            if (double.IsNaN(ms) || ms <= 0) return 0;
            for (int i = 0; i < _bucketEdges.Length; i++)
            {
                if (ms <= _bucketEdges[i]) return i;
            }
            return _bucketEdges.Length - 1;
        }

        private static long NowSec() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private Timeline GetTimeline(string key)
        {
            if (!_map.TryGetValue(key, out var t))
            {
                t = new Timeline(bucketCount: _bucketEdges.Length);
                _map[key] = t;
            }
            return t;
        }

        // 以 IOPS 作为权重更新直方图
        public void Update(string key, double? readLatencyMs, double? writeLatencyMs, double? readIops, double? writeIops)
        {
            if ((!readLatencyMs.HasValue || !readIops.HasValue || readIops.GetValueOrDefault() <= 0)
                && (!writeLatencyMs.HasValue || !writeIops.HasValue || writeIops.GetValueOrDefault() <= 0))
                return;

            lock (_lock)
            {
                var t = GetTimeline(key);
                var now = NowSec();
                // 取当前秒帧
                Frame? cur = t.Frames.LastOrDefault();
                if (cur == null || cur.Second != now)
                {
                    cur = new Frame(t.BucketCount) { Second = now };
                    t.Frames.AddLast(cur);
                }
                // 修剪过期帧
                while (t.Frames.Count > 0 && (now - t.Frames.First!.Value.Second) > _windowSeconds)
                    t.Frames.RemoveFirst();

                if (readLatencyMs.HasValue && readIops.HasValue && readIops.Value > 0)
                {
                    int b = FindBucket(readLatencyMs.Value);
                    cur.Read[b] += readIops.Value;
                }
                if (writeLatencyMs.HasValue && writeIops.HasValue && writeIops.Value > 0)
                {
                    int b = FindBucket(writeLatencyMs.Value);
                    cur.Write[b] += writeIops.Value;
                }
            }
        }

        private static (double? p50, double? p95, double? p99) PercentilesFromBuckets(IReadOnlyList<double> buckets, IReadOnlyList<double> edges)
        {
            double total = buckets.Sum();
            if (total <= 0) return (null, null, null);
            double c = 0;
            double? p50 = null, p95 = null, p99 = null;
            for (int i = 0; i < buckets.Count; i++)
            {
                c += buckets[i];
                double pct = c / total;
                if (p50 == null && pct >= 0.50) p50 = edges[i];
                if (p95 == null && pct >= 0.95) p95 = edges[i];
                if (p99 == null && pct >= 0.99) { p99 = edges[i]; break; }
            }
            return (p50, p95, p99);
        }

        public (double? read_p50, double? read_p95, double? read_p99, double? write_p50, double? write_p95, double? write_p99) GetPercentiles(string key)
        {
            lock (_lock)
            {
                if (!_map.TryGetValue(key, out var t) || t.Frames.Count == 0)
                    return (null, null, null, null, null, null);
                var now = NowSec();
                // 修剪
                while (t.Frames.Count > 0 && (now - t.Frames.First!.Value.Second) > _windowSeconds)
                    t.Frames.RemoveFirst();
                // 汇总
                var read = new double[t.BucketCount];
                var write = new double[t.BucketCount];
                foreach (var f in t.Frames)
                {
                    for (int i = 0; i < t.BucketCount; i++)
                    {
                        read[i] += f.Read[i];
                        write[i] += f.Write[i];
                    }
                }
                var (rp50, rp95, rp99) = PercentilesFromBuckets(read, _bucketEdges);
                var (wp50, wp95, wp99) = PercentilesFromBuckets(write, _bucketEdges);
                return (rp50, rp95, rp99, wp50, wp95, wp99);
            }
        }
    }
}
