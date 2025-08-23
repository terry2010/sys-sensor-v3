# 网络指标开发计划（Windows）

本文件定义“网络（Network）”模块的指标集合、字段命名（snake_case）、单位、数据来源与回退策略、采样与聚合、前后端契约、测试策略与验收标准。完成后需与 `doc/istat-menus-metrics.md` 的“5. 网络”逐条对照，确保无遗漏（平台不适用项返回 null 并注明）。

---

## 1. 指标分组与字段定义

说明：Windows 环境下尽量对齐 iStat Menus 语义；macOS/Wi‑Fi 专属或驱动依赖强的项在 Windows 下标记为 null 或映射为等价概念。

### 1.1 速率与总量（Totals & Rates）
- io_totals（所有网络接口总览：Network Interface\_Total）
  - rx_bytes_per_sec: number
  - tx_bytes_per_sec: number
  - rx_packets_per_sec: number | null
  - tx_packets_per_sec: number | null
  - rx_errors_per_sec: number | null
  - tx_errors_per_sec: number | null
  - rx_drops_per_sec: number | null
  - tx_drops_per_sec: number | null

- per_interface_io[]（每接口实例：Network Interface(<instance>)）
  - if_id: string  // 按 IfIndex/Guid 组合
  - name: string
  - rx_bytes_per_sec: number
  - tx_bytes_per_sec: number
  - rx_packets_per_sec: number | null
  - tx_packets_per_sec: number | null
  - rx_errors_per_sec: number | null
  - tx_errors_per_sec: number | null
  - rx_drops_per_sec: number | null
  - tx_drops_per_sec: number | null
  - utilization_percent: number | null  // 以协商速率为分母估算

- usage_totals（用量汇总，滚动窗口/计费周期）
  - today_rx_bytes: number | null
  - today_tx_bytes: number | null
  - week_rx_bytes: number | null
  - week_tx_bytes: number | null
  - month_rx_bytes: number | null
  - month_tx_bytes: number | null
  - billing_cycle_rx_bytes: number | null
  - billing_cycle_tx_bytes: number | null

### 1.2 接口通用信息（Interface Info）
- per_interface_info[]
  - if_id: string  // IfIndex/Guid
  - name: string
  - type: string | null  // ethernet/wifi/loopback/vpn/tunnel/bridge/cellular/virtual
  - status: string | null  // up/down/unknown
  - mac_address: string | null
  - mtu: number | null
  - ipv4: string[] | null  // 含掩码/前缀可选单独字段
  - ipv6: string[] | null
  - gateway: string[] | null
  - dns_servers: string[] | null
  - search_domains: string[] | null
  - rx_errors: number | null
  - tx_errors: number | null
  - collisions: number | null

### 1.3 Wi‑Fi 详情（Wi‑Fi Details, 若可用）
- wifi_info（当前首选 WLAN 接口；多接口场景可扩展为数组）
  - if_id: string | null
  - ssid: string | null
  - bssid: string | null  // AP MAC
  - band_mhz: number | null  // 2400/5000/6000
  - channel: number | null
  - channel_width_mhz: number | null  // 20/40/80/160
  - phy_mode: string | null  // 802.11 a/b/g/n/ac/ax
  - security: string | null  // WPA2/WPA3/Enterprise 等
  - rssi_dbm: number | null
  - noise_dbm: number | null
  - snr_db: number | null
  - tx_phy_rate_mbps: number | null
  - country_code: string | null

### 1.4 以太网/Thunderbolt 网卡（Ethernet/TB）
- per_ethernet_info[]
  - if_id: string
  - link_speed_mbps: number | null  // 10/100/1000/2500/5000/10000...
  - duplex: string | null  // half/full/unknown
  - auto_negotiation: boolean | null

### 1.5 虚拟/隧道接口（Virtual/Tunnel）
- per_virtual_info[]
  - if_id: string
  - kind: string | null  // vpn/tun/tap/utun/bridge/hotspot
  - underlying_if: string | null

### 1.6 连通性与诊断（Connectivity）
- connectivity
  - public_ipv4: string | null
  - public_ipv6: string | null
  - last_changed_ts: number | null  // UTC ms
  - ping_targets[]: { host: string, rtt_ms: number | null, jitter_ms: number | null, loss_percent: number | null }

### 1.7 进程榜（增强项）
- top_processes_by_network[]（上/下行分列建议在前端展示）
  - pid: number
  - name: string
  - rx_bytes_per_sec: number
  - tx_bytes_per_sec: number

---

## 2. 数据来源与回退策略（Windows）

- Performance Counters（优先，用于实时速率）
  - Category: "Network Interface"
  - Counters: "Bytes Received/sec"、"Bytes Sent/sec"、"Packets Received/sec"、"Packets Sent/sec"、"Packets Received Errors"、"Packets Outbound Errors" 等
  - 采样节流：>= 200ms（与现有 `NetCounters` 一致）
  - 回退：若计数器不可用，返回 0 或 null，并做一次 Debug 降级日志

- IP Helper API（GetIfTable2/IfOperStatus/速度）
  - 协商速率（`Transmit/ReceiveLinkSpeed`）、MTU、接口类型/状态
  - IPv4/IPv6 地址、网关、DNS（配合 `GetAdaptersAddresses`）

- WMI/CIM
  - `MSFT_NetAdapter`、`MSFT_NetIPAddress`、`MSFT_DNSClientServerAddress`
  - `Win32_NetworkAdapter` / `Win32_NetworkAdapterConfiguration`

- Wi‑Fi（WLAN API / netsh 回退）
  - 原生 `Wlan*` API 读取 SSID/BSSID/信道/带宽/PHY/安全/RSSI/噪声/速率
  - 回退：`netsh wlan show interfaces` 解析（本地化/多语言需适配）

- 以太网/Thunderbolt
  - 通过 IP Helper 或 `MSFT_NetAdapter` 获取 `LinkSpeed`、`Duplex`（若驱动提供）

- 虚拟/隧道
  - 通过 `MSFT_NetAdapter` 类型与名称模式识别（vpn/tun/tap/bridge 等）

- 连通性
  - 公网 IP：HTTP GET 到公共服务（例如 ipify）；无网络/被拦截时返回 null
  - Ping：`System.Net.NetworkInformation.Ping`，计算 RTT/抖动/丢包

- 进程榜（增强）
  - 优先 ETW（`Microsoft-Windows-Kernel-Network`）聚合进程级上下行速率
  - 回退：不实现或仅暴露系统总览，字段置 null

---

## 3. 采样、聚合与命名

- 命名：统一 snake_case；单位在本文件注明
- 粒度：
  - 实时：总览 io_totals + per_interface_io
  - 静态/配置：per_interface_info + per_ethernet_info + wifi_info + per_virtual_info
  - 连通性：connectivity（按需启用）
- 节流：实时计数器 200–500ms；静态信息缓存 30–60s 或按变更事件刷新
- 历史与用量：
  - `HistoryStore` 保留必要速率；
  - 用量统计按天/周/月/计费周期滚动累加（应用重启后从持久化恢复）

---

## 4. 后端实现计划（分阶段）

- 阶段 A（基础实时速率与总览）
  1) 扩展 `NetCounters`：Bytes/Packets/sec、错误/丢包计数
  2) `NetworkCollector`：输出 io_totals / per_interface_io

- 阶段 B（接口静态与配置）
  1) 新增 `NetworkQuery`：IP Helper + WMI/CIM 汇总 per_interface_info
  2) 以太网协商速率/双工（若可读）：per_ethernet_info

- 阶段 C（Wi‑Fi 详情）
  1) 接入 WLAN API，填充 `wifi_info`
  2) 回退实现：`netsh` 解析（可禁用）

- 阶段 D（连通性与用量）
  1) 公网 IP 查询（可选，频率限流）
  2) Ping 监测（目标列表可配置，默认关闭）
  3) 用量统计与持久化（today/week/month/billing）

- 阶段 E（进程榜，增强）
  1) ETW 订阅与聚合，输出 `top_processes_by_network`

---

## 5. 测试计划

- 单元测试（`SystemMonitor.Tests`）
  - [计数器与节流] 模拟 PerfCounter 不可用/异常 → 返回零/空且无抖动
  - [聚合准确性] per_interface 汇总 ≈ totals（允许微小误差）
  - [命名/契约] 字段名/可空性/单位校验

- 集成测试
  - [IP Helper/WMI] per_interface_info 字段类型正确且可读
  - [WLAN] Wi‑Fi 信息在有无线网卡时返回；无网卡时为 null

- 端到端（E2E）
  - 通过 JSON‑RPC 订阅 `metrics`，校验网络模块字段完整性与数值范围
  - 启动 `scripts/dev.ps1`，在 UI 中显示上/下行速率与接口状态

- 合约与架构测试
  - JSON Schema/契约测试：新增/变更字段需更新并校验
  - 性能回归：高吞吐与多接口下 CPU 占用可控，无内存泄漏

---

## 6. 验收标准

- 字段覆盖达到本计划“1. 指标分组与字段定义”的全部项；平台不适用项返回 null 并在文档中注明
- 在网络持续变化环境下，速率与用量曲线稳定，无明显抖动或异常尖刺
- 接口状态/配置在变更后能在 60s 内刷新
- E2E 演示：主窗口展示上/下行速率与接口信息；日志无异常堆栈

---

## 7. 与 iStat Menus 清单对照（无遗漏声明）

- 覆盖项：
  - 速率与总量：实时上/下行（总览与每接口）、历史/用量（本计划提供滚动统计）
  - 接口信息：名称/状态/MAC/IPv4/IPv6/网关/DNS/MTU/错误/丢包/碰撞
  - Wi‑Fi：SSID/BSSID/频段/信道/带宽/PHY/安全/RSSI/噪声/SNR/速率
  - 以太网/TB：链路速率、双工、自协商
  - 虚拟/隧道：VPN/桥接/热点等识别
  - 连通性：公网 IP、Ping（可选）
  - 进程榜：按上/下行分列（增强）
- 平台差异（保留字段但在 Windows 返回 null，或后续增强）：
  - 部分驱动不提供错误/丢包/噪声/信道宽度/双工等 → 置 null
  - 进程级网络速率：默认关闭，仅在 ETW 可用且用户授权时启用

---

## 8. 开发工作流（逐指标落实）

1) 在 `doc/todo-YYYY-MM-DD.md` 补充本模块条目，为每批指标打勾并写明来源/回退
2) 修改后端（按阶段 A→E 逐步合入）
3) 本地编译直到通过
4) 修改/新增测试并反复运行直到通过（单元/集成/契约/E2E）
5) 前端（Tauri）主进程临时打印网络指标并在面板展示
6) 使用 `scripts/dev.ps1` 启动联调，确认打印值合理
7) 移除临时打印
8) 重读 `doc/istat-menus-metrics.md`，选择下一模块

---

## 9. 风险与回退

- 某些计数器或 WMI 类在用户环境不可用：值置 0 或 null，并降级日志
- WLAN API 在未安装无线网卡/驱动场景不可用：返回 null
- 驱动不提供链路双工/错误统计：对应字段置 null
- 进程级网络统计依赖 ETW/权限：默认关闭，无法启用则不返回该部分
