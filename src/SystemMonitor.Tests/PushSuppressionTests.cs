using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SystemMonitor.Service.Services;
using Xunit;

namespace SystemMonitor.Tests
{
    public class PushSuppressionTests
    {
        private static RpcServer NewServer()
        {
            var logger = NullLoggerFactory.Instance.CreateLogger("test");
            ILogger<HistoryStore> storeLogger = NullLogger<HistoryStore>.Instance;
            var store = new HistoryStore(storeLogger);
            // 不初始化 SQLite 也可以，AppendAsync 里会忽略空路径
            var server = new RpcServer(logger, 0, store, Guid.NewGuid());
            return server;
        }

        [Fact]
        public void SuppressWindow_Should_Block_Push_Temporarily()
        {
            var s = NewServer();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.False(s.IsPushSuppressed(now));
            s.SuppressPush(200);
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.True(s.IsPushSuppressed(now));
            Thread.Sleep(260);
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Assert.False(s.IsPushSuppressed(now));
        }

        [Fact]
        public async Task SubscribeMetrics_Should_Enable_Push()
        {
            var s = NewServer();
            // 订阅前关闭
            var r0 = await s.subscribe_metrics(new SubscribeMetricsParams { enable = false });
            Assert.NotNull(r0);
            Assert.False(s.MetricsPushEnabled);
            // 开启后为 true（是否桥接在 RpcHostedService 中判断，这里仅验证开关）
            var r1 = await s.subscribe_metrics(new SubscribeMetricsParams { enable = true });
            Assert.NotNull(r1);
            Assert.True(s.MetricsPushEnabled);
        }

        [Fact]
        public async Task Start_SetConfig_Should_Update_Intervals_And_Modules()
        {
            var s = NewServer();
            // 默认启用 cpu/memory
            var mods = s.GetEnabledModules();
            Assert.Contains("cpu", mods, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("memory", mods, StringComparer.OrdinalIgnoreCase);

            // start 仅含 cpu
            await s.start(new StartParams { modules = new[] { "cpu" } });
            mods = s.GetEnabledModules();
            Assert.Contains("cpu", mods, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("memory", mods);

            // set_config 设置模块间隔
            var cfg = await s.set_config(new SetConfigParams
            {
                base_interval_ms = 1000,
                module_intervals = new System.Collections.Generic.Dictionary<string, int> { ["cpu"] = 150 }
            });
            Assert.NotNull(cfg);
            var delay = s.GetCurrentIntervalMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.True(delay <= 150 && delay >= 50); // 受最小 50ms 与模块最小间隔影响
        }
    }
}
