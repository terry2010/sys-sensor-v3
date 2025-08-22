using System;
using System.Threading.Tasks;

namespace SystemMonitor.Service.Services
{
    // RpcServer 的推送抑制窗口实现
    internal sealed partial class RpcServer
    {
        // 在处理前台 RPC 响应期间抑制 metrics 推送的时间点（毫秒时间戳，now < _suppressUntil 时抑制）
        private long _suppressUntil;

        public bool IsPushSuppressed(long now)
        {
            lock (_lock) { return now < _suppressUntil; }
        }

        public void SuppressPush(int ms)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var until = now + Math.Max(50, ms);
            lock (_lock)
            {
                _suppressUntil = Math.Max(_suppressUntil, until);
            }
        }

        /// <summary>
        /// 等待抑制窗口结束（最多等待 maxWaitMs）。用于通知前避免与当前 RPC 响应交叉。
        /// </summary>
        /// <param name="maxWaitMs">最大等待时长（毫秒），默认 500ms。</param>
        /// <param name="pollMs">轮询间隔（毫秒），默认 20ms。</param>
        private async Task WaitForUnsuppressedAsync(int maxWaitMs = 500, int pollMs = 20)
        {
            var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Max(0, maxWaitMs);
            while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < deadline)
            {
                if (!IsPushSuppressed(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return;
                try { await Task.Delay(Math.Max(5, pollMs)).ConfigureAwait(false); } catch { return; }
            }
        }
    }
}
