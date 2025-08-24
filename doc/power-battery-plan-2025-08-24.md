# 电池与电源指标开发计划（2025-08-24）

本计划面向 `doc/istat-menus-metrics.md` 第7节“电池与电源（Battery & Power）”。目标是在 Windows 10 上按 snake_case 字段实现以下子模块：battery（电池）、adapter（适配器/供电）、ups（不间断电源，若识别为电池设备）。

—

## 1. 指标范围与字段定义（与指标文档对齐）

模块名：`power`

- battery（电池状态与健康）
  - percentage: number 0..100（电量百分比）
  - state: 'discharging' | 'charging' | 'full' | 'ac' | 'unknown'（供电/充放状态）
  - time_remaining_min: number | null（剩余使用时间，分钟，估算）
  - time_to_full_min: number | null（充满时间，分钟，估算）
  - cycle_count: number | null（循环次数）
  - condition: 'normal' | 'replace_soon' | 'replace_now' | 'unknown' | null（健康状况）
  - full_charge_capacity_mah: number | null（满充容量）
  - design_capacity_mah: number | null（设计容量）
  - voltage_mv: number | null（端电压，毫伏）
  - current_ma: number | null（电流，mA；充电为正，放电为负）
  - power_w: number | null（瞬时功率，瓦）
  - manufacturer: string | null（制造商）
  - serial_number: string | null（序列号）
  - manufacture_date: string | null（YYYY-MM-DD 或原始字符串）
  - time_on_battery_sec: number | null（自上次断电以来用电池时长，秒）
  - ac_line_online: boolean | null（是否接入外部电源）

- adapter（适配器/供电，若可读）
  - present: boolean | null（是否连接）
  - rated_watts: number | null（额定功率，W）
  - negotiated_watts: number | null（协商功率，W；USB-PD）
  - voltage_mv: number | null（电压，mV）
  - current_ma: number | null（电流，mA）
  - is_pd_fast_charge: boolean | null（是否 PD 快充）
  - charge_mode: 'charging' | 'maintenance' | 'full' | 'unknown' | null（充电阶段/策略）

- ups（UPS/外部电源，若系统识别为电池设备）
  - present: boolean | null
  - percentage: number | null
  - runtime_min: number | null（预估续航）
  - power_source: 'ac' | 'battery' | 'unknown' | null
  - input_voltage_v: number | null
  - input_frequency_hz: number | null
  - load_percent: number | null

说明：以上字段名遵循 snake_case，若不可读则返回 null，不抛出异常。

—

## 2. Windows 平台数据来源与优先级

- 基础供电状态（AC/电量/剩余时间）
  - GetSystemPowerStatus（Win32 API）
    - ACLineStatus → ac_line_online / state
    - BatteryFlag/BatteryLifePercent/BatteryLifeTime/BatteryFullLifeTime → percentage、time_remaining_min、time_to_full_min（推断）
  - 回退/补充：WMI `Win32_Battery`（EstimatedChargeRemaining、BatteryStatus、EstimatedRunTime）

- 电池健康/容量/循环次数等（可用性因厂商而异）
  - WMI `root\WMI`：`BatteryStaticData`、`BatteryStatus`、`BatteryFullChargedCapacity`、`BatteryCycleCount`、`MSBattery_Class` 等（存在性因机型/驱动差异大）
  - 部分笔记本厂商提供专有 WMI/SDK（本期不引入，若不可读则返回 null）

- 电压/电流/功率
  - `root\WMI` 下 `BatteryStatus` 的 Voltage/Rate/Current（字段名随驱动差异，注意容错）
  - 若仅有电流与电压，可计算 `power_w = (voltage_mv/1000) * (current_ma/1000)`（四舍五入两位）

- 适配器/PD 协商信息
  - Windows 原生读取受限。可尝试：`Win32_PnPEntity` + 设备属性、`USB\VID_*`，或 `Power Delivery` 相关接口（限制较多）。
  - 本期策略：尽力从 WMI/设备属性推断，失败则返回 null。

- UPS
  - 若 UPS 驱动将其呈现为电池设备，可通过 `Win32_Battery` 或 `UPS` 相关类读取。实际兼容性有限，本期返回 null 或基本电量。

—

## 3. 后端实现方案

- 新增采集器：`src/SystemMonitor.Service/Services/Collectors/PowerCollector.cs`
  - `ModuleName`: "power"
  - `RecommendedInterval`: 1000ms（与其他核心模块对齐）
  - `IsAvailable()`: 检测是否存在电池或可用电源信息（GetSystemPowerStatus 成功即视为可用）
  - `CollectAsync()`: 
    - 首选 GetSystemPowerStatus 组装基础字段
    - 通过 WMI（`System.Management`）补齐高级字段（容量、循环次数、电压/电流等），对缺失字段返回 null
    - 异常不抛出，记录 Debug 日志并返回已有字段
  - 内部节流：≥400ms 窗口复用缓存，避免高频 WMI 访问

- P/Invoke 扩展：`Win32Interop`
  - 新增 `GetSystemPowerStatus` 结构体与签名

- 注册采集器：`MetricsRegistry` 静态构造中 `Register(new PowerCollector())`（建议在 `SensorCollector` 前）

- RPC 输出：
  - `snapshot()`/`metrics` 推流对象增加 `power: { battery: {...}, adapter?: {...} | null, ups?: {...} | null }`
  - 历史存储：M1 不落盘，仅实时展示（后续如需在 `HistoryStore` 增加 `power` 表）

—

## 4. 测试计划（SystemMonitor.Tests）

- 单元测试
  - `PowerCollectorTests`
    - 无电池/桌面机：`IsAvailable()` 可为 true（AC 状态可读），battery.percentage 可能为 null 或 100；不抛异常
    - 字段命名 snake_case（契约校验）
    - 容错：WMI 查询失败时返回已有字段且不抛
    - 数值范围：percentage ∈ [0,100]；时间/容量/电压/电流/功率为 >=0 或 null
  - `SchemaTests`/`ContractTests`：`snapshot()` 包含 `power` 且 key 为 snake_case

- 端到端
  - `EndToEndTests`：`hello` + `start(['power'])` 后收到带 `power.battery.percentage` 或至少 `power.battery.state`

—

## 5. 前端改动（Tauri + Vue3）

- DTO：`frontend/src/api/dto.ts` 扩展 `SnapshotResult` 增加 `power` 模块类型
- Store：`frontend/src/stores/metrics.ts` 的 `MetricPoint` 增加 `power?: { battery: ... }`
- 组件：
  - `SnapshotPanel.vue` 能显示 `power` 原始 JSON（现有通用渲染已支持）
  - 临时日志：在 `src-tauri/src/main.rs` 订阅 `metrics` 时打印 `power.battery.percentage/state`（联调后注释掉）

- 联调：使用 `scripts/dev.ps1` 启动，验证日志与 UI 显示

—

## 6. 开发顺序与里程碑映射

1) 冻结字段（本文档）
2) 新增 `PowerCollector` + Win32Interop 扩展 + 注册
3) 反复编译修正直至通过
4) 新增/完善测试并通过
5) 前端 DTO/Store/显示与主进程临时打印
6) `scripts/dev.ps1` 启动联调，确认输出合理
7) 注释临时打印，重读 `doc/istat-menus-metrics.md`，选择下一项

—

## 7. 风险与回退

- WMI 类在不同机型/驱动下可用性差：逐字段 try-catch 返回 null
- 桌面机无电池：`battery` 多数字段为 null，仅 `ac_line_online` 与 `state='ac'`
- 适配器/PD 信息：大概率不可得，先留 null，不影响 UI
- UPS：兼容性弱，先返回 null 或基本电量

—

## 8. 验收标准

- 控制台/服务模式下 `snapshot()` 返回包含 `power`，核心字段正确
- 端到端事件桥 `metrics` 至少每秒输出一次 `power.battery.state`，如有电池则输出 `percentage`
- 单元/集成测试全部通过
- 前端能显示 `power`，并在联调中主进程打印符合预期（随后注释）
