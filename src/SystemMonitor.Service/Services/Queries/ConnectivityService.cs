using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace SystemMonitor.Service.Services.Queries
{
    /// <summary>
    /// ConnectivityService：提供公网 IP 与 Ping 目标的连通性信息。
    /// - 公网 IP：限流查询（默认 60s），失败返回 null；追踪最近变更时间戳。
    /// - Ping：对若干目标执行一次简易 Ping（超时 800ms），计算 RTT；jitter/loss 首版置 null。
    /// - 线程安全缓存，避免频繁外部请求。
    /// </summary>
    internal sealed class ConnectivityService
    {
        private static readonly Lazy<ConnectivityService> _inst = new(() => new ConnectivityService());
        public static ConnectivityService Instance => _inst.Value;

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        private readonly object _lock = new();
        private object? _cache; // { connectivity = { ... } }
        private long _cacheAt;

        private string? _lastIpv4;
        private string? _lastIpv6;
        private long? _lastChangedTs;

        private const int PublicIpTtlMs = 60_000;
        private const int PingTimeoutMs = 800;

        public object Read(bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force)
            {
                lock (_lock)
                {
                    if (_cache != null && now - _cacheAt < 5_000)
                        return _cache; // 整体 5s 缓存
                }
            }

            var (ipv4, ipv6) = TryQueryPublicIp(now);
            var pingTargets = TryPingTargets();

            var payload = new
            {
                connectivity = new
                {
                    public_ipv4 = ipv4,
                    public_ipv6 = ipv6,
                    last_changed_ts = _lastChangedTs,
                    ping_targets = pingTargets,
                }
            };

            lock (_lock)
            {
                _cache = payload; _cacheAt = now;
            }
            return payload;
        }

        private (string? ipv4, string? ipv6) TryQueryPublicIp(long now)
        {
            // 限流：60s 内最多查询一次
            bool needQuery = (_lastChangedTs == null) || (now - _cacheAt > PublicIpTtlMs);
            if (!needQuery)
            {
                return (_lastIpv4, _lastIpv6);
            }

            string? v4 = _lastIpv4, v6 = _lastIpv6;
            try
            {
                // 先 IPv4
                v4 = TryGet("https://api.ipify.org?format=json", "ip");
            }
            catch { }
            try
            {
                v6 = TryGet("https://api64.ipify.org?format=json", "ip");
            }
            catch { }

            // 结果格式校验：若 api64 返回的不是 IPv6（例如仅有 IPv4 环境），置空
            v4 = IsValidIPv4(v4) ? v4 : null;
            v6 = IsValidIPv6(v6) ? v6 : null;

            if (v4 != _lastIpv4 || v6 != _lastIpv6)
            {
                _lastChangedTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            _lastIpv4 = v4; _lastIpv6 = v6;
            return (v4, v6);
        }

        private static string? TryGet(string url, string prop)
        {
            try
            {
                var rsp = _http.GetStringAsync(url).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(rsp);
                if (doc.RootElement.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String)
                    return e.GetString();
            }
            catch { }
            return null;
        }

        private static object[] TryPingTargets()
        {
            var targets = new[] { "1.1.1.1", "8.8.8.8", "114.114.114.114" };
            var list = new List<object>();
            foreach (var host in targets)
            {
                try
                {
                    using var ping = new Ping();
                    var reply = ping.Send(host, PingTimeoutMs);
                    double? rtt = null;
                    if (reply != null && reply.Status == IPStatus.Success)
                    {
                        rtt = reply.RoundtripTime;
                    }
                    list.Add(new { host = host, rtt_ms = rtt, jitter_ms = (double?)null, loss_percent = (double?)null });
                }
                catch
                {
                    list.Add(new { host = host, rtt_ms = (double?)null, jitter_ms = (double?)null, loss_percent = (double?)null });
                }
            }
            return list.ToArray();
        }

        private static bool IsValidIPv4(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            return System.Net.IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        }

        private static bool IsValidIPv6(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            return System.Net.IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }
    }
}
