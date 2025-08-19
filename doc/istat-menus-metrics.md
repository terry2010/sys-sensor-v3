# iStat Menus 可监控指标完整清单（去重整理）

说明：本清单仅列“监控指标”（read-only 或可读数值），不包含“组合视图/通知/交互控制”等功能项。不同机型（Intel/Apple Silicon）、系统版本、驱动/权限与外设会影响可见项；带“若可用/视机型”的指标并非所有 Mac 都有。

---

## 1. CPU（中央处理器）
- 总览
  - 使用率：用户（User）/系统（System）/空闲（Idle）/Nice
  - 平均负载（Load Average）：1/5/15 分钟
  - 进程数、线程数
  - 运行时间（Uptime）
  - 顶部占用进程（按 CPU%）
- 每核/分组
  - 各物理/逻辑核心使用率与历史迷你图
  - Apple Silicon：性能核（P‑cores）/能效核（E‑cores）分组使用率（若可用）
- 频率/时钟（若可用）
  - 当前/最低/最高频率、倍频/Bus（部分 Intel 机型）
- 热/功（常与“传感器”重叠，由此处聚合展示）
  - CPU Die/Package/Proximity 温度（若可用）
  - 包功率与各轨功率（IA/GT/Uncore/DRAM 等；若可用）
 - 内核活动（若可用）
   - 上下文切换（Context Switches）/系统调用（Syscalls）/中断（Interrupts）

## 2. GPU（图形处理器）
- 使用率：集显/独显/Apple Silicon GPU/eGPU（若可用）
- 显存（VRAM）占用与上限（若驱动提供）
- 频率/功耗/温度（若驱动提供）
- 活跃 GPU 状态（双显卡切换，若可用）
- 顶部占用进程（按 GPU，若可用）
- Apple Neural Engine（ANE）使用率（Apple Silicon，若可用）

## 3. 内存（Memory）
- 容量与分类
  - 总量、已用、可用（Available）
  - App 内存、有线（Wired）、压缩（Compressed）、缓存文件（Cached Files）
  - 可清除（Purgeable）缓存（APFS 环境常见）
- 压力与虚拟内存
  - 内存压力（Memory Pressure）
  - 交换区（Swap）使用量与活动速率（若可用）
  - 页面换入/换出（Page‑ins/outs 及速率，若可用）
  - 内存压缩/解压速率（Compression/Decompression Rate，若可用）
  - 页面错误（Page Faults，若可用）/页面清除（Page Purges，若可用）
  - 历史曲线（压力/已用/压缩/Swap 活动）
- 进程榜：按内存占用排序的顶部进程

## 4. 磁盘与存储（Disks）
- 容量/卷信息
  - 每块物理磁盘、APFS 容器与卷：总量、已用、可用
  - 文件系统类型（APFS/HFS+/ExFAT 等）、挂载点、读写/只读状态
  - 加密状态（FileVault/加密卷，若可用）
  - 可清理（Purgeable）空间（APFS）
  - APFS 本地快照（Time Machine Local Snapshots）大小与数量（Count，若可读）
  - VM/Swapfiles 占用（位于 APFS 容器内，若可读）
- 实时 I/O 与活动
  - 读/写速度（KB/s/MB/s）与历史曲线
  - 读/写 IOPS（若可用）
  - 忙碌/队列时间比例（若可用）
  - 队列深度与平均/峰值延迟（Queue Depth/Latency，若可用）
  - 顶部占用进程：按磁盘 I/O（读/写分列）
- 设备/接口信息（若可读）
  - 型号、固件版本、序列号
  - 介质类型（SSD/HDD/融合）、接口类型（NVMe/SATA/USB/Thunderbolt）
  - 机械硬盘转速（RPM，若可用）、TRIM 支持/启用状态
  - 外接设备在线状态与弹出（Eject）能力（若为可移除介质）
  - 总线/链路速率与协商模式（如 USB/Thunderbolt/NVMe 链路速率，若可读）
- 健康/温度（S.M.A.R.T. 与 NVMe 日志，若支持）
  - S.M.A.R.T. 总体健康
  - 常见 SATA 属性（如 Power‑On Hours、Reallocated/Pending/UDMA CRC、温度等）
  - 常见 NVMe 属性（如 Percentage Used、Data Units Read/Written、Controller Busy Time、Unsafe Shutdowns、温度与热节流状态等）
  - 设备/控制器温度（复合/多探头，若有）/SSD 控制器温度（若可读）

## 5. 网络（Network）
- 速率与总量
  - 实时上/下行带宽（总览与每接口）
  - 当日/周/月/计费周期用量统计（总量与按接口）
  - 历史迷你图（峰值、采样/平滑可调）
- 接口通用信息（每网络服务/接口）
  - 名称、状态（连接/断开）、MAC 地址
  - IPv4/IPv6 地址（本地/全局）、子网掩码、路由器/网关、DNS 服务器、搜索域（Search Domains，若可读）
  - MTU、错误/丢包/碰撞计数（若驱动提供）
- Wi‑Fi 详情（若可用）
  - SSID、BSSID（AP MAC）
  - 频段与信道、带宽（20/40/80/160 MHz）
  - PHY 模式（802.11 a/b/g/n/ac/ax）与安全类型（WPA2/WPA3/企业）
  - RSSI、噪声、SNR
  - 协商速率/传输速率（Tx/PHY Rate）、国家码/监管域
- 以太网/Thunderbolt 网卡
  - 链路速率（10/100/1000/2.5G/5G/10G）、双工模式
- 虚拟/隧道接口
  - VPN（utun/tun/tap）、桥接、共享、个人热点等的独立统计
- 连通性
  - 外网 IP（IPv4/IPv6）与变更记录（可触发通知）
  - Ping/延迟监控（单/多目标，抖动、丢包、历史）
- 进程榜：按网络上/下行分列的顶部进程

## 6. 传感器与风扇（Sensors & Fans）
- 温度（示例，实际名称/数量依机型与 SMC/SoC 暴露）
  - CPU：Die/Core/Package/Proximity、Heatsink
  - GPU：Core/Diode/Proximity
  - 平台/芯片组：PCH/ICH/Northbridge/Memory Controller
  - 存储：SSD/NVMe/HDD 温度
  - 主板/VRM/Thunderbolt 控制器等
  - 机身/散热：进风/出风、散热片、掌托、触控板、底壳、Ambient（环境）
  - Apple Silicon：SoC、CPU/GPU/Memory/Thunderbolt/PD 控制器、底壳多点等
- 风扇
  - 每个风扇实时转速（RPM）与历史
  - 最小/最大/目标转速（读数；部分 Intel 机型支持控制）
  - 风扇占空比 Duty（若硬件导出）
- 电压/电流/功率（若硬件提供）
  - 适配器 DC‑in 电压/电流、协商功率（USB‑C PD，若可读）
  - 系统总功率（W）
  - CPU/GPU/SoC 包功率与各电源轨（3.3V/5V/12V/1.xV 等）
  - 电池充/放电电流（mA）、瞬时功率（W）
- 其他传感器（若存在）
  - 环境光传感器（ALS）
  - 气流、湿度（部分机型）
  - 各类接口/控制器温度（Wi‑Fi/BT/Thunderbolt/VRM 等）
  - T2/安全芯片温度、显示 TCON 温度（若可读）
  - 机箱开合/霍尔传感器状态（若可读）

## 7. 电池与电源（Battery & Power）
- 电池状态与健康
  - 电量百分比、供电状态（放电/充电/已满/外接）
  - 剩余使用时间/充满时间（估算）
  - 循环次数（Cycle Count）、健康状况（Condition）
  - 满充容量（Full Charge）与设计容量（Design Capacity）
- 电池电学与信息（若可读）
  - 温度、端电压、电流（充/放电，正/负值）、瞬时功率（W）
  - 制造商、序列号、制造日期
  - 本次用电池时长（Time on Battery，自上次断电以来，若可读）
- 适配器/供电（若可读）
  - 适配器额定/协商功率、电压/电流档位、是否 PD 快充
  - 充电策略/阶段（充电/维持/已满，若可读）
- UPS/外部电源（若系统识别为电池设备）
  - 电量、预估续航、当前电源来源
  - 输入电压/频率/负载（取决于驱动）

## 8. 时间 / 日历（Time & Date）
- 本地时间（可自定义格式）、12/24 小时、是否显示秒/周数/年内序号（Day of Year）
- 世界时钟：多城市/多时区并列
  - 支持 UTC/本地/自定义时区标签
- 日历事件列表：当天/即将发生/未来事件、倒计时（来自“日历”App）
- 日出/日落时间（也可来源于天气模块）
 - 曙光/暮光时间（Dawn/Dusk，若可用）
 - 月相（Moon Phase）

## 9. 天气（Weather）
- 当前状况
  - 实况温度、体感温度（Feels Like）、最低/最高
  - 天气现象图标（晴/云/雨/雪/雾等）
  - 降水：概率/类型（雨/雪/冰雹）/强度（分钟级若数据源支持）
  - 风：风速、阵风、风向；风级换算
  - 相对湿度、云量、能见度
  - 气压与趋势箭头
  - 紫外线指数（UV）、露点（Dew Point）
  - 空气质量（AQI，若数据源/地区支持）
  - 日出/日落、昼长
  - 潮汐（Tides，若数据源支持）
  - 月出/月落时间（若数据源支持）
- 预报
  - 小时级：温度/降水/风/UV 等
  - 多日：最高/最低、降水概率、风等
- 天气预警/提醒（数据源支持区域）

## 10. 外设电量（Peripherals/Bluetooth）
- 蓝牙/部分 2.4G 或 USB 外设的电量百分比与在线状态
  - Apple 键盘/鼠标/触控板、AirPods/耳机、兼容手柄等
  - 部分 USB 外设（若系统将其呈现为电池设备）

## 11. 系统信息（System Info）
- 机器与操作系统
  - 机型标识/型号、序列号（若可读）
  - CPU 型号/核心数量、内存容量与频率/通道（若可读）
  - 图形设备列表（集显/独显/eGPU 型号）
  - macOS 版本/构建号
  - SMC/固件/Boot ROM 版本（若可读）
- 网络与标识（部分与“网络”重复，此处为静态概览）
  - 主机名、硬件 MAC 地址（概要）
  - 本机/外网 IP 概览

---

注：
- “组合视图（Combined）”“通知规则（Notifications）”属于展示/告警功能，不在本清单的“监控指标”范畴内。
- 传感器键名（如 SMC 的 TC0D/TG0P/F0Ac 等）在界面以友好名呈现，且不同机型差异巨大，iStat Menus 会自动枚举可读传感器。
