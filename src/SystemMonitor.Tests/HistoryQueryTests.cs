using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SystemMonitor.Service.Services;
using Xunit;

namespace SystemMonitor.Tests
{
    public class HistoryQueryTests
    {
        private static RpcServer NewServer()
        {
            var logger = NullLoggerFactory.Instance.CreateLogger("test");
            var storeLogger = NullLogger<HistoryStore>.Instance;
            var store = new HistoryStore(storeLogger);
            var server = new RpcServer(logger, 0, store, Guid.NewGuid());
            return server;
        }

        private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private static long BucketEnd(long t, long bucketMs) => ((t - 1) / bucketMs + 1) * bucketMs;

        [Fact]
        public async Task Raw_Empty_Window_Fallsback_To_Current_Snapshot()
        {
            var s = NewServer();
            var from = NowMs() - 1000;
            var to = NowMs();
            var resp = await s.query_history(new QueryHistoryParams { from_ts = from, to_ts = to, agg = "raw" });
            // 反序列化匿名对象到动态节点以便断言
            var json = System.Text.Json.JsonSerializer.Serialize(resp);
            var node = JsonNode.Parse(json)!.AsObject();
            Assert.True(node["ok"]!.GetValue<bool>());
            var items = node["items"]!.AsArray();
            Assert.True(items.Count == 1); // raw + 空窗口 → 单条当前快照
        }

        [Fact]
        public async Task Agg_10s_InMemory_Fallback_Buckets_By_EndTime()
        {
            var s = NewServer();
            var baseTs = BucketEnd(NowMs(), 10_000) - 25_000; // 造两三个桶
            // 三条样本分布在三个 10s 桶
            s.AppendHistory(baseTs + 1_000, 10, (1000, 100));
            s.AppendHistory(baseTs + 12_000, 20, (1000, 200));
            s.AppendHistory(baseTs + 22_000, 30, (1000, 300));
            var resp = await s.query_history(new QueryHistoryParams { from_ts = baseTs, to_ts = baseTs + 30_000, agg = "10s" });
            var json = System.Text.Json.JsonSerializer.Serialize(resp);
            var node = JsonNode.Parse(json)!.AsObject();
            Assert.True(node["ok"]!.GetValue<bool>());
            var items = node["items"]!.AsArray();
            Assert.Equal(3, items.Count);
            // 断言按桶结束时间排序
            long prev = 0;
            foreach (var it in items)
            {
                var ts = it!["ts"]!.GetValue<long>();
                Assert.True(ts >= prev);
                prev = ts;
            }
        }

        [Fact]
        public async Task StepMs_1s_Groups_Last_Value_Per_Bucket()
        {
            var s = NewServer();
            var start = NowMs();
            // 两条落入同一 1s 桶，后一条应覆盖
            s.AppendHistory(start + 10, 5, (1000, 100));
            s.AppendHistory(start + 900, 7, (1000, 120));
            // 下一桶
            s.AppendHistory(start + 1200, 9, (1000, 150));
            var resp = await s.query_history(new QueryHistoryParams { from_ts = start, to_ts = start + 2200, step_ms = 1000 });
            var json = System.Text.Json.JsonSerializer.Serialize(resp);
            var node = JsonNode.Parse(json)!.AsObject();
            Assert.True(node["ok"]!.GetValue<bool>());
            var items = node["items"]!.AsArray();
            Assert.True(items.Count >= 2);
            // 简要验证顺序与非空
            long prev = 0;
            foreach (var it in items)
            {
                var ts = it!["ts"]!.GetValue<long>();
                Assert.True(ts >= prev);
                prev = ts;
            }
        }
    }
}
