using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using SystemMonitor.Service.Services.Interop;

namespace SystemMonitor.Service.Services.Queries
{
    /// <summary>
    /// WifiQuery：读取当前 WLAN 接口的 Wi‑Fi 详情（首版：解析 `netsh wlan show interfaces`）。
    /// - 若无无线网卡/命令失败：返回 null。
    /// - 字段命名统一为 snake_case（外部序列化策略负责转换）。
    /// - 采用简单缓存以避免频繁调用外部命令。
    /// </summary>
    internal sealed class WifiQuery
    {
        private static readonly Lazy<WifiQuery> _inst = new(() => new WifiQuery());
        public static WifiQuery Instance => _inst.Value;

        private object? _cache; // { wifi_info = { ... } | null }
        private long _cacheAt;
        private readonly object _lock = new();
        private const int TtlMs = 10_000; // 10s

        public object Read(bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force)
            {
                lock (_lock)
                {
                    if (_cache != null && now - _cacheAt < TtlMs)
                        return _cache;
                }
            }

            // 优先 WLAN API，失败或字段缺失则用 netsh 补全
            object? wlan = TryReadViaWlan();
            object? netsh = null;
            if (wlan == null)
            {
                netsh = TryReadViaNetsh();
                object payload0 = new { wifi_info = netsh };
                lock (_lock) { _cache = payload0; _cacheAt = now; }
                return payload0;
            }
            // 若 WLAN 存在但字段为空，尝试合并 netsh
            netsh = TryReadViaNetsh();
            object merged = MergePreferLeft(wlan, netsh);
            object payload = new { wifi_info = merged };

            lock (_lock)
            {
                _cache = payload; _cacheAt = now;
            }
            return payload;
        }

        private static object? TryReadViaNetsh()
        {
            try
            {
                string? windir = Environment.GetEnvironmentVariable("WINDIR") ?? Environment.SystemDirectory;
                string sys32 = System.IO.Path.Combine(Environment.SystemDirectory, "netsh.exe");
                string sysnative = System.IO.Path.Combine(windir ?? "C:\\Windows", "Sysnative", "netsh.exe");

                string[] candidates = Environment.Is64BitProcess
                    ? new[] { sys32, "netsh" }
                    : new[] { sysnative, sys32, "netsh" };

                string output = string.Empty;
                string error = string.Empty;
                int exit = -1;
                Exception? lastEx = null;

                // 优先：通过 cmd 切换到 UTF-8 代码页，强制 netsh 输出 UTF-8，彻底避免乱码
                try
                {
                    var psiUtf8 = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c chcp 65001 >nul & netsh wlan show interfaces",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                    };
                    using var pUtf8 = Process.Start(psiUtf8);
                    if (pUtf8 != null)
                    {
                        var outUtf8 = pUtf8.StandardOutput.ReadToEnd();
                        var errUtf8 = pUtf8.StandardError.ReadToEnd();
                        pUtf8.WaitForExit(3000);
                        if (!string.IsNullOrWhiteSpace(outUtf8) || !string.IsNullOrWhiteSpace(errUtf8))
                        {
                            output = outUtf8; error = errUtf8; exit = pUtf8.ExitCode;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }

                // 回退：直接调用 netsh（多路径），用系统 ANSI 读取
                if (string.IsNullOrWhiteSpace(output) && string.IsNullOrWhiteSpace(error))
                {
                    foreach (var file in candidates)
                    {
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = file,
                                Arguments = "wlan show interfaces",
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                            };
                            try { psi.StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); } catch { }
                            using var p = Process.Start(psi);
                            if (p == null) { lastEx = new InvalidOperationException("Process.Start returned null"); continue; }
                            output = p.StandardOutput.ReadToEnd();
                            error = p.StandardError.ReadToEnd();
                            p.WaitForExit(3000);
                            exit = p.ExitCode;
                            if (!string.IsNullOrWhiteSpace(output) || !string.IsNullOrWhiteSpace(error)) break;
                        }
                        catch (Exception ex)
                        {
                            lastEx = ex;
                            continue;
                        }
                    }
                }
                if (string.IsNullOrWhiteSpace(output) && string.IsNullOrWhiteSpace(error))
                {
                    // 仍然返回带诊断信息的对象，避免上层看不到 netsh_raw 字段
                    return new
                    {
                        name = (string?)null,
                        if_id = (string?)null,
                        ssid = (string?)null,
                        bssid = (string?)null,
                        band_mhz = (long?)null,
                        channel = (long?)null,
                        channel_width_mhz = (long?)null,
                        phy_mode = (string?)null,
                        security = (string?)null,
                        rssi_dbm = (long?)null,
                        noise_dbm = (long?)null,
                        snr_db = (long?)null,
                        tx_phy_rate_mbps = (long?)null,
                        country_code = (string?)null,
                        signal_quality = (long?)null,
                        netsh_raw = (string?)null,
                        netsh_parsed = new { },
                        netsh_error = lastEx != null ? lastEx.GetType().Name + ": " + lastEx.Message : ($"exit={exit}"),
                    };
                }

                // 解析关键字段（注意本地化差异，这里匹配多语言关键字的一部分，保持尽力而为）
                // 常见英文关键字：
                //   SSID, BSSID, Network type, Radio type, Authentication, Channel, Receive rate (Mbps), Transmit rate (Mbps), Signal
                // 中文本地化（参考）：
                //   SSID, BSSID, 网络类型, 无线电类型, 身份验证, 信道, 接收速率 (Mbps), 传输速率 (Mbps), 信号
                static string? FindLineValue(IEnumerable<string> lines, params string[] keys)
                {
                    foreach (var line in lines)
                    {
                        var t = line.Trim();
                        foreach (var k in keys)
                        {
                            var idx = t.IndexOf(k, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                var pos = t.IndexOf(':');
                                if (pos > 0 && pos + 1 < t.Length)
                                {
                                    return t.Substring(pos + 1).Trim();
                                }
                            }
                        }
                    }
                    return null;
                }

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var name   = FindLineValue(lines, "Name", "接口名称", "名称");
                var ssid   = FindLineValue(lines, "SSID");
                var bssid  = FindLineValue(lines, "BSSID");
                var radio  = FindLineValue(lines, "Radio type", "无线电类型");
                var auth   = FindLineValue(lines, "Authentication", "身份验证");
                var channel= FindLineValue(lines, "Channel", "信道", "频道");
                var rxRate = FindLineValue(lines, "Receive rate", "接收速率");
                var txRate = FindLineValue(lines, "Transmit rate", "传输速率");
                var signal = FindLineValue(lines, "Signal", "信号");

                long? toLong(string? s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    var num = new string(s.Where(ch => char.IsDigit(ch)).ToArray());
                    if (long.TryParse(num, out var v)) return v;
                    return null;
                }

                double? toDouble(string? s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    // 保留数字与小数点，兼容逗号小数
                    var sb = new StringBuilder();
                    foreach (var ch in s)
                    {
                        if (char.IsDigit(ch) || ch == '.' || ch == ',') sb.Append(ch == ',' ? '.' : ch);
                    }
                    if (double.TryParse(sb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                    return null;
                }

                // 频段推断：根据信道粗略判断（2.4G/5G/6G）
                long? bandMhz = null; long? channelNum = toLong(channel);
                if (channelNum.HasValue)
                {
                    var ch = channelNum.Value;
                    if (ch >= 1 && ch <= 14) bandMhz = 2400;
                    else if (ch >= 32 && ch <= 177) bandMhz = 5000; // 简化
                    else if (ch >= 1 && ch <= 233) bandMhz = null; // 无法稳定判断 6GHz，置空
                }

                // PHY 模式：直接用 netsh 的 Radio type（如 802.11n/ac/ax）
                string? phyMode = radio;

                // 安全类型：直接透传 Authentication
                string? security = auth;

                // Tx PHY 速率（Mbps）：取 Transmit rate
                // 优先解析为 double，再四舍五入为 long
                long? txPhyRateMbps = null;
                var txd = toDouble(txRate);
                if (txd.HasValue) txPhyRateMbps = (long)Math.Round(txd.Value);

                // RSSI/噪声/SNR：netsh 默认不提供，置 null（后续可用 WLAN API）
                long? rssiDbm = null; long? noiseDbm = null; long? snrDb = null;

                // 信道带宽：netsh 未直接提供，置 null
                long? channelWidthMhz = null;

                // if_id 暂无可靠映射（可使用与连接匹配的接口名进一步解析），首版置 null
                string? ifId = null;

                // Signal（%）
                long? signalQuality = toLong(signal);

                // 不再因 SSID/BSSID 为空而早退，至少返回 netsh_raw 供诊断/补全

                var netshParsed = new
                {
                    name = name,
                    ssid = ssid,
                    bssid = bssid,
                    radio = radio,
                    authentication = auth,
                    channel = channelNum,
                    receive_rate_mbps = toLong(rxRate),
                    transmit_rate_mbps = txPhyRateMbps,
                    signal_quality = signalQuality,
                };

                return new
                {
                    name = name,
                    if_id = ifId,
                    ssid = ssid,
                    bssid = bssid,
                    band_mhz = bandMhz,
                    channel = channelNum,
                    channel_width_mhz = channelWidthMhz,
                    phy_mode = phyMode,
                    security = security,
                    rssi_dbm = rssiDbm,
                    noise_dbm = noiseDbm,
                    snr_db = snrDb,
                    tx_phy_rate_mbps = txPhyRateMbps,
                    country_code = (string?)null,
                    signal_quality = signalQuality,
                    netsh_raw = output,
                    netsh_parsed = netshParsed,
                    netsh_error = string.IsNullOrWhiteSpace(error) ? null : error,
                };
            }
            catch
            {
                // 兜底：即使异常也返回基本对象，保证字段可见
                return new
                {
                    name = (string?)null,
                    if_id = (string?)null,
                    ssid = (string?)null,
                    bssid = (string?)null,
                    band_mhz = (long?)null,
                    channel = (long?)null,
                    channel_width_mhz = (long?)null,
                    phy_mode = (string?)null,
                    security = (string?)null,
                    rssi_dbm = (long?)null,
                    noise_dbm = (long?)null,
                    snr_db = (long?)null,
                    tx_phy_rate_mbps = (long?)null,
                    country_code = (string?)null,
                    signal_quality = (long?)null,
                    netsh_raw = (string?)null,
                    netsh_parsed = new { },
                    netsh_error = "exception",
                };
            }
        }

        private static object? TryReadViaWlan()
        {
            IntPtr h = IntPtr.Zero;
            IntPtr pIfList = IntPtr.Zero;
            try
            {
                uint ver;
                if (WlanApi.WlanOpenHandle(2, IntPtr.Zero, out ver, out h) != 0 || h == IntPtr.Zero)
                    return null;
                if (WlanApi.WlanEnumInterfaces(h, IntPtr.Zero, out pIfList) != 0 || pIfList == IntPtr.Zero)
                    return null;

                var ifs = WlanApi.ReadInterfaces(pIfList);
                // 仅在存在“已连接”的接口时继续；否则回退
                var target = ifs.FirstOrDefault(x => x.isState == WlanApi.WLAN_INTERFACE_STATE.wlan_interface_state_connected);
                if (target.InterfaceGuid == Guid.Empty) return null;

                // 查询当前连接属性
                int dataSize;
                IntPtr pData;
                if (WlanApi.WlanQueryInterface(h, ref target.InterfaceGuid, WlanApi.WLAN_INT_OPCODE.wlan_intf_opcode_current_connection, IntPtr.Zero, out dataSize, out pData, IntPtr.Zero) != 0 || pData == IntPtr.Zero)
                    return null;
                var conn = Marshal.PtrToStructure<WlanApi.WLAN_CONNECTION_ATTRIBUTES>(pData);
                WlanApi.WlanFreeMemory(pData);

                // 未连接则返回 null
                if (conn.isState != WlanApi.WLAN_INTERFACE_STATE.wlan_interface_state_connected)
                    return null;

                // 提取连接属性
                var ssid = WlanApi.SsidToString(conn.wlanAssociationAttributes.dot11Ssid);
                var bssid = WlanApi.MacToString(conn.wlanAssociationAttributes.dot11Bssid.ucDot11MacAddress);
                var phy = MapPhy(conn.wlanAssociationAttributes.dot11PhyType);
                var txRateMbps = SafeToMbps(conn.wlanAssociationAttributes.ulTxRate);
                var signalQ = (long?)conn.wlanAssociationAttributes.wlanSignalQuality; // 0..100

                // 获取 BSS 列表以获得 RSSI、中心频率与 IE（推断信道带宽/国家码）
                long? rssiDbm = null; long? channel = null; long? bandMhz = null; long? chWidthMhz = null; string? country = null; string? ieHex = null;
                // 初始化为空列表，保证返回结构中始终带有 wlan_bss 字段
                List<object>? bssList = new List<object>();
                try
                {
                    var ssidStruct = conn.wlanAssociationAttributes.dot11Ssid;
                    // 若当前连接未返回 SSID，则以空 SSID 扫描（any）
                    if (string.IsNullOrEmpty(ssid))
                    {
                        ssidStruct.uSSIDLength = 0;
                        ssidStruct.ucSSID = ssidStruct.ucSSID ?? new byte[32];
                    }
                    IntPtr pBssList;
                    // 放宽参数：bssType 使用 any，bSecurityEnabled=false 以提升兼容性（部分驱动严格过滤可能返回空）
                    var bssType = WlanApi.DOT11_BSS_TYPE.dot11_BSS_type_any;
                    if (WlanApi.WlanGetNetworkBssList(h, ref target.InterfaceGuid, ref ssidStruct, bssType, false, IntPtr.Zero, out pBssList) == 0 && pBssList != IntPtr.Zero)
                    {
                        var bsses = WlanApi.ReadBssListWithIe(pBssList);
                        // 组装简表（最多8条）
                        bssList = new List<object>(Math.Min(8, bsses.Length));
                        for (int i = 0; i < bsses.Length && i < 8; i++)
                        {
                            var be = bsses[i];
                            var freqMhz = be.Entry.chCenterFrequency / 1000UL;
                            var chPair = FreqMhzToChannel((long)freqMhz);
                            bssList.Add(new
                            {
                                bssid = WlanApi.MacToString(be.Entry.dot11Bssid),
                                ssid = WlanApi.SsidToString(be.Entry.dot11Ssid),
                                rssi_dbm = (long?)be.Entry.rssi,
                                freq_mhz = (long)freqMhz,
                                channel = chPair.channel,
                                ie_len = (int)be.Entry.ieSize,
                            });
                        }
                        // 优先匹配相同 BSSID 的项
                        WlanApi.WLAN_BSS_ENTRY_MANAGED? pick = null;
                        foreach (var e in bsses)
                        {
                            var mac = WlanApi.MacToString(e.Entry.dot11Bssid);
                            if (!string.IsNullOrEmpty(bssid) && string.Equals(mac, bssid, StringComparison.OrdinalIgnoreCase)) { pick = e; break; }
                            if (pick == null && WlanApi.SsidToString(e.Entry.dot11Ssid) == ssid) pick = e;
                        }
                        if (pick == null && bsses.Length > 0) pick = bsses[0];
                        if (pick.HasValue)
                        {
                            var e = pick.Value;
                            rssiDbm = e.Entry.rssi;
                            // chCenterFrequency 单位 KHz -> MHz
                            var mhz = e.Entry.chCenterFrequency / 1000UL;
                            var ch = FreqMhzToChannel((long)mhz);
                            channel = ch.channel;
                            bandMhz = ch.band_mhz;

                            // 解析 IE：HT(61)/VHT(192)/Country(7)
                            try
                            {
                                var ies = e.IeBytes;
                                if (ies != null && ies.Length > 0)
                                {
                                    (long? cw, string? cc) = ParseWifiIe(ies);
                                    chWidthMhz = cw ?? chWidthMhz;
                                    country = string.IsNullOrWhiteSpace(cc) ? country : cc;
                                    // 原始 IE 十六进制
                                    var sb = new StringBuilder(ies.Length * 2);
                                    foreach (var b in ies) sb.Append(b.ToString("X2"));
                                    ieHex = sb.ToString();
                                }
                            }
                            catch { }
                        }
                        WlanApi.WlanFreeMemory(pBssList);
                    }
                }
                catch { }

                return new
                {
                    name = target.strInterfaceDescription,
                    if_id = target.InterfaceGuid.ToString("D"),
                    ssid = string.IsNullOrWhiteSpace(ssid) ? null : ssid,
                    bssid = string.IsNullOrWhiteSpace(bssid) ? null : bssid,
                    band_mhz = bandMhz,
                    channel = channel,
                    channel_width_mhz = chWidthMhz,
                    phy_mode = phy,
                    security = MapSecurity(conn.wlanSecurityAttributes),
                    rssi_dbm = rssiDbm,
                    noise_dbm = (long?)null,
                    snr_db = (long?)null,
                    tx_phy_rate_mbps = txRateMbps,
                    country_code = country,
                    signal_quality = signalQ,
                    wlan_ie = ieHex,
                    wlan_bss = (object?)bssList,
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                if (pIfList != IntPtr.Zero) try { WlanApi.WlanFreeMemory(pIfList); } catch { }
                if (h != IntPtr.Zero) try { WlanApi.WlanCloseHandle(h, IntPtr.Zero); } catch { }
            }
        }

        private static long? SafeToMbps(uint bps)
        {
            try { if (bps == 0) return null; return (long)Math.Round(bps / 1_000_000.0); } catch { return null; }
        }

        private static (long? channel, long? band_mhz) FreqMhzToChannel(long mhz)
        {
            // 常见映射：2.4G: 2412+5*(ch-1); 5G: 常见 5180, 5200, ...; 6G: 5955 起
            if (mhz >= 2412 && mhz <= 2484)
            {
                var ch = 1 + (int)Math.Round((mhz - 2412) / 5.0);
                return (ch, 2400);
            }
            if (mhz >= 5170 && mhz <= 5895)
            {
                // 以 5180MHz 对应 36 信道为基准
                var ch = 36 + (int)Math.Round((mhz - 5180) / 5.0);
                return (ch, 5000);
            }
            if (mhz >= 5925 && mhz <= 7125)
            {
                // 6GHz 以 5955MHz 对应 1 信道（简化）
                var ch = 1 + (int)Math.Round((mhz - 5955) / 5.0);
                return (ch, 6000);
            }
            return (null, null);
        }

        private static string? MapPhy(WlanApi.DOT11_PHY_TYPE t)
        {
            return t switch
            {
                WlanApi.DOT11_PHY_TYPE.dot11_phy_type_ht => "802.11n",
                WlanApi.DOT11_PHY_TYPE.dot11_phy_type_vht => "802.11ac",
                WlanApi.DOT11_PHY_TYPE.dot11_phy_type_he => "802.11ax",
                WlanApi.DOT11_PHY_TYPE.dot11_phy_type_eht => "802.11be",
                WlanApi.DOT11_PHY_TYPE.dot11_phy_type_ofdm => "802.11a/g",
                WlanApi.DOT11_PHY_TYPE.dot11_phy_type_dsss => "802.11b",
                _ => null,
            };
        }

        private static string? MapSecurity(WlanApi.WLAN_SECURITY_ATTRIBUTES s)
        {
            // 仅粗略映射
            return s.dot11AuthAlgorithm switch
            {
                WlanApi.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3 or WlanApi.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA3_SAE => "WPA3",
                WlanApi.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_RSNA or WlanApi.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_RSNA_PSK => "WPA2",
                WlanApi.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA or WlanApi.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_WPA_PSK => "WPA",
                WlanApi.DOT11_AUTH_ALGORITHM.DOT11_AUTH_ALGO_80211_OPEN => s.dot11CipherAlgorithm == WlanApi.DOT11_CIPHER_ALGORITHM.DOT11_CIPHER_ALGO_NONE ? "Open" : "WEP/Other",
                _ => null,
            };
        }

        // 解析 802.11 信息元素（IEs）：返回 (channel_width_mhz, country_code)
        private static (long? cw, string? cc) ParseWifiIe(byte[] ies)
        {
            long? vhtWidth = null; // 80/160/80+80
            long? htWidth = null;  // 40
            string? country = null;
            for (int i = 0; i + 1 < ies.Length; )
            {
                byte id = ies[i++];
                int len = ies[i++];
                if (len < 0 || i + len > ies.Length) break;
                // 元素数据范围 [i, i+len)
                try
                {
                    switch (id)
                    {
                        case 7: // Country
                            if (len >= 3)
                            {
                                // 前 3 字节为国家码（ASCII）
                                var c1 = (char)ies[i];
                                var c2 = (char)ies[i + 1];
                                var c3 = (char)ies[i + 2];
                                string cc = new string(new[] { c1, c2 });
                                if (char.IsLetter(cc[0]) && char.IsLetter(cc[1]))
                                {
                                    country = cc.ToUpperInvariant();
                                }
                            }
                            break;
                        case 61: // HT Operation
                            // 结构：primary channel (1B), ht_info_subset_1 (1B), ht_info_subset_2 (1B), ht_info_subset_3 (1B) ...
                            if (len >= 2)
                            {
                                byte subset1 = ies[i + 1];
                                // bit0-1 Secondary Channel Offset: 0=none, 1=above, 3=below
                                int sco = subset1 & 0x03;
                                if (sco == 1 || sco == 3) htWidth = 40;
                            }
                            break;
                        case 192: // VHT Operation
                            // 结构：channel width (1B): 0=20/40, 1=80, 2=160, 3=80+80
                            if (len >= 1)
                            {
                                byte w = ies[i];
                                vhtWidth = w switch
                                {
                                    1 => 80,
                                    2 => 160,
                                    3 => 160, // 80+80 视作 160
                                    _ => (long?)null,
                                };
                            }
                            break;
                        default:
                            break;
                    }
                }
                catch { }
                i += len;
            }
            long? width = vhtWidth ?? htWidth; // 优先 VHT，其次 HT
            return (width, country);
        }

        // 左优先合并：左对象的非空值保留，右对象用于填补空(null/空字符串/0信号质量等)
        private static object MergePreferLeft(object left, object? right)
        {
            var l = ToDict(left);
            var r = right == null ? new Dictionary<string, object?>() : ToDict(right);
            foreach (var kv in r)
            {
                if (!l.TryGetValue(kv.Key, out var lv) || IsNullish(lv))
                {
                    l[kv.Key] = kv.Value;
                }
            }
            return l;
        }

        private static bool IsNullish(object? v)
        {
            if (v == null) return true;
            if (v is string s) return string.IsNullOrWhiteSpace(s);
            // 对于信号质量 0 视为可被替换（表示未知/未填充）
            if (v is long l && l == 0) return true;
            if (v is string s2 && s2.Replace(":", string.Empty).Trim('0').Length == 0 && s2.Contains(':')) return true; // 00:00:... 视为空
            return false;
        }

        private static Dictionary<string, object?> ToDict(object obj)
        {
            if (obj is System.Collections.IDictionary id)
            {
                var dict = new Dictionary<string, object?>();
                foreach (var key in id.Keys)
                {
                    if (key is string sk) dict[sk] = id[key];
                }
                return dict;
            }
            var res = new Dictionary<string, object?>();
            var props = obj.GetType().GetProperties();
            foreach (var p in props)
            {
                res[p.Name] = p.GetValue(obj);
            }
            return res;
        }
    }
}
