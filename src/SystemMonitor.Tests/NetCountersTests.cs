using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using SystemMonitor.Service.Services.Collectors;
using Xunit;

namespace SystemMonitor.Tests
{
    public class NetCountersTests
    {
        private static readonly JsonSerializerOptions Snake = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false
        };

        [Fact]
        public void NetCounters_Read_Contract_And_Aggregation()
        {
            var obj = NetCounters.Instance.Read();
            // 转 JSON，便于统一以 snake_case 访问字段
            var node = JsonSerializer.SerializeToNode(obj, Snake) as JsonObject;
            Assert.NotNull(node);

            // 顶层结构
            Assert.True(node!.TryGetPropertyValue("io_totals", out var totalsNode) && totalsNode is JsonObject);
            Assert.True(node!.TryGetPropertyValue("per_interface_io", out var perArray) && perArray is JsonArray);
            var totals = (JsonObject)totalsNode!;
            var arr = (JsonArray)perArray!;

            long SumOrZero(JsonNode? n)
            {
                return (n is JsonValue v && v.TryGetValue<long>(out var l)) ? l : 0L;
            }
            long? SumNullable(JsonNode? n)
            {
                return (n is JsonValue v && v.TryGetValue<long>(out var l)) ? l : (long?)null;
            }

            // 必选：Bytes/sec 聚合应等于 per_interface 求和
            long sumRxBytes = 0, sumTxBytes = 0;
            foreach (var it in arr.OfType<JsonObject>())
            {
                sumRxBytes += SumOrZero(it["rx_bytes_per_sec"]);
                sumTxBytes += SumOrZero(it["tx_bytes_per_sec"]);
            }
            Assert.Equal(sumRxBytes, SumOrZero(totals["rx_bytes_per_sec"]));
            Assert.Equal(sumTxBytes, SumOrZero(totals["tx_bytes_per_sec"]));
            Assert.True(sumRxBytes >= 0 && sumTxBytes >= 0);

            // 可选计数器：若 per_interface 全为 null -> totals 应为 null；否则 totals 为非负且等于非空项之和
            void AssertOptionalSum(string rxKey, string txKey)
            {
                long sumRx = 0, sumTx = 0; int cntRx = 0, cntTx = 0;
                foreach (var it in arr.OfType<JsonObject>())
                {
                    var r = SumNullable(it[rxKey]); if (r.HasValue) { sumRx += r.Value; cntRx++; }
                    var t = SumNullable(it[txKey]); if (t.HasValue) { sumTx += t.Value; cntTx++; }
                }
                var totRx = SumNullable(totals[rxKey]);
                var totTx = SumNullable(totals[txKey]);
                if (cntRx == 0) Assert.Null(totRx); else { Assert.NotNull(totRx); Assert.Equal(sumRx, totRx!.Value); Assert.True(totRx!.Value >= 0); }
                if (cntTx == 0) Assert.Null(totTx); else { Assert.NotNull(totTx); Assert.Equal(sumTx, totTx!.Value); Assert.True(totTx!.Value >= 0); }
            }

            AssertOptionalSum("rx_packets_per_sec", "tx_packets_per_sec");
            AssertOptionalSum("rx_errors_per_sec", "tx_errors_per_sec");
            AssertOptionalSum("rx_drops_per_sec", "tx_drops_per_sec");

            // 字段非负性快速检查（per_interface）
            foreach (var it in arr.OfType<JsonObject>())
            {
                Assert.True(SumOrZero(it["rx_bytes_per_sec"]) >= 0);
                Assert.True(SumOrZero(it["tx_bytes_per_sec"]) >= 0);
            }
        }

        [Fact]
        public void NetCounters_Read_CacheWithin200ms()
        {
            var r1 = NetCounters.Instance.Read();
            var r2 = NetCounters.Instance.Read(); // <200ms 内，命中缓存
            var j1 = JsonSerializer.Serialize(r1, Snake);
            var j2 = JsonSerializer.Serialize(r2, Snake);
            Assert.Equal(j1, j2);
        }
    }
}
