using System;

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
    }
}
