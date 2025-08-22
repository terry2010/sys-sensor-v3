using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SystemMonitor.Service.Services;
using Xunit;

namespace SystemMonitor.Tests
{
    public class BridgeAndBurstTests
    {
        private static RpcServer NewServer()
        {
            var logger = NullLoggerFactory.Instance.CreateLogger("test");
            ILogger<HistoryStore> storeLogger = NullLogger<HistoryStore>.Instance;
            // 重置静态推流开关，避免跨测试串扰
            RpcServer.ResetForTests();
            var store = new HistoryStore(storeLogger);
            return new RpcServer(logger, 0, store, Guid.NewGuid());
        }

        [Fact]
        public async Task Hello_With_Bridge_Capability_Should_Set_Bridge_And_Enable_Push()
        {
            var s = NewServer();
            Assert.False(s.IsBridgeConnection);
            Assert.False(s.MetricsPushEnabled);
            var p = new HelloParams
            {
                app_version = "test",
                protocol_version = 1,
                token = "ok",
                capabilities = new[] { "metrics_stream" }
            };
            var res = await s.hello(p);
            Assert.NotNull(res);
            Assert.True(s.IsBridgeConnection);
            Assert.True(s.MetricsPushEnabled);
        }

        [Fact]
        public async Task Burst_Subscribe_Should_Override_Interval_Temporarily()
        {
            var s = NewServer();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // 先设置基础间隔为 1000ms
            await s.set_config(new SetConfigParams { base_interval_ms = 1000 });
            var baseInt = s.GetCurrentIntervalMs(now);
            Assert.True(baseInt >= 100 && baseInt <= 1000);

            // 订阅突发：200ms/ttl=800ms
            var r = await s.burst_subscribe(new BurstParams { interval_ms = 200, ttl_ms = 800 });
            Assert.NotNull(r);
            var burstInt = s.GetCurrentIntervalMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.InRange(burstInt, 50, 200);

            // 等待突发过期后，间隔应回退到基础/模块最小
            await Task.Delay(900);
            var after = s.GetCurrentIntervalMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.True(after >= 100); // 回退到 >= 100ms（基础保护）
        }
    }
}
