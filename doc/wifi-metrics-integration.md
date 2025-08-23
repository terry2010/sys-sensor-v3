# Wi‑Fi 指标集成与经验总结

本篇记录 SystemMonitor Wi‑Fi 指标的设计、实现要点、编码/互操作坑点与排障方法，供后续维护参考。

## 目标与原则
- 优先使用 Windows WLAN API 获取高质量数据；以 `netsh` 作为回退与补齐源。
- JSON 字段统一 `snake_case`，并尽量全量保留原始与解析后的调试信息。
- 尽量不返回“空值假信息”，例如全 0 的 BSSID、0 的信号质量等应视为无效。 

## 数据来源与合并
- WLAN API（`wlanapi.dll`）：
  - 连接属性（SSID/BSSID/PHY/TxRate/SignalQuality/Security）。
  - BSS 列表（RSSI、中心频率、IE 原始字节）。
- netsh：`netsh wlan show interfaces`
  - 接口名、SSID、BSSID、Radio Type、Authentication、Channel、Rx/Tx 速率、Signal（%）。

合并策略 `MergePreferLeft(left=wlan, right=netsh)`：
- 左值优先；当左值为 null/空字符串/`00:00:...`/`0`（部分字段）时，用右值补齐。

## 关键实现文件
- `src/SystemMonitor.Service/Services/Queries/WifiQuery.cs`
  - `TryReadViaWlan()`：
    - 调用 `WlanOpenHandle / WlanEnumInterfaces / WlanQueryInterface` 获取连接信息。
    - 调用 `WlanGetNetworkBssList` 并解析 `WlanApi.ReadBssListWithIe()` 结果。
    - IE 解析：Country(7) → `country_code`；HT(61)/VHT(192) → `channel_width_mhz`。
    - 始终返回 `wlan_bss` 字段（为空数组也返回）。
  - `TryReadViaNetsh()`：
    - 强制 UTF‑8：`cmd /c chcp 65001 >nul & netsh wlan show interfaces`，读取 `StandardOutputEncoding = UTF8`，避免跨语言乱码。
    - 失败回退：尝试 `Sysnative/System32/netsh.exe` + 系统 ANSI 读取。
    - 始终返回 `netsh_raw`、`netsh_parsed`、`netsh_error`（用于诊断）。
  - `MergePreferLeft()` 与 `IsNullish()`：处理空/无效值。
  - 10s 简单缓存以降低调用频率。
- `src/SystemMonitor.Service/Services/Interop/WlanApi.cs`
  - P/Invoke 定义与结构体。
  - `ReadBssListWithIe()`：读取 `WLAN_BSS_ENTRY` + IE 原始字节。
- `src/SystemMonitor.Service/Services/Collectors/NetworkCollector.cs`
  - 保留 `wifi_info` 的全部字段，仅覆盖 `if_id`；避免白名单重建导致字段丢失。

## 主要坑点与解决方案
- netsh 输出乱码：
  - 现象：中文系统/混合区域设置下 `netsh_raw` 乱码，解析缺失。
  - 方案：强制 UTF‑8 代码页（`chcp 65001`）后再执行 netsh；若仍失败，返回 `netsh_error` 诊断。
- 字段被丢：
  - 现象：前端看不到 `netsh_*`/`wlan_*` 字段。
  - 根因：`NetworkCollector` 重建对象时使用了白名单，覆盖了 `wifi_info`。
  - 方案：反射复制全部属性，仅改写 `if_id`。
- BSS 列表为空：
  - 方案：`WlanGetNetworkBssList` 放宽为 `dot11_BSS_type_any + bSecurityEnabled=false`；仍为空则由 `netsh_parsed` 补齐关键字段。
- 伪空值：
  - 处理：`00:00:...` BSSID、`signal_quality==0`、`channel==0` 等视作可被替换。

## 字段清单（概要）
- 顶层：`name`, `if_id`, `ssid`, `bssid`, `phy_mode`, `security`, `signal_quality`, `rssi_dbm`, `tx_phy_rate_mbps`, `band_mhz`, `channel`, `channel_width_mhz`, `country_code`。
- 调试：`netsh_raw`, `netsh_parsed`, `netsh_error`, `wlan_ie`, `wlan_bss[]`。

## 测试要点
- 在不同系统语言下验证（中文/英文/日文）：`netsh_raw` 无乱码、`netsh_parsed` 字段完整。
- 连接/断开 Wi‑Fi、2.4G/5G/6G、不同路由器（n/ac/ax）场景下验证字段完整性。
- 验证缓存：多次请求间隔 <10s 时命中缓存；>10s 时刷新数据。

## 排障清单
- 若无 `netsh_raw`：
  - 检查 `netsh_error`；在 PowerShell 中执行 `netsh wlan show interfaces`，确认权限/路径。
- 若 `wlan_bss` 长期为空：
  - 可能为驱动限制；确认接口已连接，或尝试管理员权限。
- 若 `bssid` 仍为 `00:...`：
  - 贴出完整 `wifi_info`，检查合并是否命中 `netsh_parsed.bssid`。

## 变更记录
- 2025-08-24：引入 UTF‑8 强制输出、保留字段修复、BSS 参数放宽，问题验证通过。
