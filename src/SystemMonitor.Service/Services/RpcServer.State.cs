using System;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace SystemMonitor.Service.Services
{
    // RpcServer 的状态与桥接通知相关实现
    internal sealed partial class RpcServer
    {
        // 发送桥接层事件（如 bridge_error/bridge_disconnected）。
        // 注意：若连接已断开，通知可能无法送达。
        internal void NotifyBridge(string @event, object payload)
        {
            try
            {
                _ = _rpc?.NotifyAsync(@event, payload);
            }
            catch { /* 忽略通知失败 */ }
        }

        // 发送最小版 state 事件（通知）。字段：ts, phase, 可选 reason/extra。
        private void EmitState(string phase, string? reason = null, object? extra = null)
        {
            try
            {
                var payload = new
                {
                    ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    phase,
                    reason,
                    extra
                };
                _ = _rpc?.NotifyAsync("state", payload);
                _logger.LogInformation("state emitted: phase={Phase} reason={Reason}", phase, reason);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "emit state failed (ignored)");
            }
        }
    }
}
