# 外设电量（蓝牙/无线外设）指标开发计划（2025-08-24）

本计划面向 `doc/istat-menus-metrics.md` 的“Peripherals/Bluetooth & Battery”相关能力，定义在 Windows 10 桌面环境（非打包 UWP）下实现“外设电量”采集与展示的字段契约、数据源与技术路线。目标是在现有后端/前端框架内，尽可能覆盖常见蓝牙/无线外设（键鼠、耳机、手柄等）的电量百分比。

—

## 1. 模块与字段（契约冻结，snake_case）

新增模块：`peripherals`

- batteries: 数组，每个元素代表一个可读电量的外设（蓝牙/2.4G 接收器/HID 等）。
  - id: string（设备稳定标识，优先 Device Instance Id / BLE Address，形如 `USB\\VID_***` 或 `BLE:XX:XX:..`）
  - name: string | null（友好名称，如设备名/产品名）
  - kind: 'bt_le' | 'bt_classic' | 'usb_hid' | 'dongle_24g' | 'unknown'
  - connection: 'connected' | 'disconnected' | 'unknown'
  - battery_percent: number 0..100 | null（电量百分比，null 表示不可得）
  - battery_state: 'normal' | 'charging' | 'discharging' | 'full' | 'unknown' | null（若可从特征/报告推断）
  - source: 'gatt_0x180f_0x2a19' | 'hid_feature' | 'driver_wmi' | 'inbox_hint' | 'unknown'
  - last_update_ts: number（Unix ms，服务端读取到该设备电量的时间）

说明：
- 若无任何可读外设，返回 `batteries: []`（空数组），不抛错。
- 字段命名严格 snake_case，对齐项目通用约束。

### 1.1 扩展字段（全部可选、可为空）

为“尽量展示更多外设信息”，在不影响核心字段的前提下，追加以下可选字段；若不可得则为 null：

- 设备标识与拓扑
  - device_instance_id: string|null（PnP 实例ID，如 `USB\VID_046D&PID_C52B\...`）
  - interface_path: string|null（设备接口路径，调试用）
  - address: string|null（BLE 地址，如 `FC:12:34:56:78:9A`）
  - transport: 'ble' | 'bt_classic' | 'usb' | 'rf_24g' | 'unknown' | null
- 厂商/固件信息
  - vendor_id: string|null（VID）
  - product_id: string|null（PID）
  - manufacturer: string|null
  - product: string|null
  - firmware_version: string|null
- 无线信号相关
  - rssi_dbm: number|null
  - tx_power_dbm: number|null
- 协议/服务能力
  - service_uuids: string[]|null（公开的服务UUID，含 0x180F 则具备电池服务）
  - supports_battery_service: boolean|null
- 连接时间线
  - connected_since_ts: number|null（Unix ms）
  - last_seen_ts: number|null（Unix ms）

—

## 2. 数据来源与优先级（Windows 桌面应用，非 UWP）

优先顺序（从上到下尝试，任一成功即可产出一个设备项）：

1) GATT 电池服务（Bluetooth LE，Battery Service 0x180F / Battery Level 0x2A19）
   - 路线A（推荐）：Win32 BthLEGatt API（BluetoothGATT*，来自 `BluetoothApis.dll`）。
     - 无需 UWP/应用打包能力声明，适用于普通桌面进程/服务。
     - 关键函数：
       - 枚举设备：SetupAPI（`SetupDiGetClassDevs`/`SetupDiEnumDeviceInfo`），接口 GUID `GUID_BLUETOOTHLE_DEVICE_INTERFACE`；
       - 打开句柄：`CreateFile` 到设备接口路径；
       - 服务与特征：`BluetoothGATTGetServices`、`BluetoothGATTGetCharacteristics`；
       - 读取数值：`BluetoothGATTGetCharacteristicValue`（解析 0..100 uint8）。
     - 兼容性：Windows 8.1+；绝大多数支持 BLE Battery Service 的键鼠/耳机可读。
   - 路线B（不采用）：WinRT `Windows.Devices.Bluetooth` + GATT（需 Appx 打包/能力声明），在非打包 Worker Service 中不可行。

2) HID Feature 报告（USB/BLE HID 设备）
   - 路线：Win32 HID API（`HidD_GetFeature`、`HidP_*`）读取厂商定义或标准电量页；
   - 适用：部分 2.4G USB 接收器/电竞鼠标提供电量特征；实现成本较高，先做预研与占位，MVP 可后置。

3) WMI/驱动扩展（厂商自带 SDK/WMI 类）
   - 路线：如 Logitech、Razer、SteelSeries 等厂商 SDK（许可/依赖复杂，当前不引入）；
   - 策略：MVP 不集成，保留 `source='driver_wmi'` 作为未来扩展点。

—

## 3. 采集器设计（后端 C#）

- 新增文件：`src/SystemMonitor.Service/Services/Collectors/PeripheralBatteryCollector.cs`
  - `Name`: "peripherals"
  - 调度：默认 10s 周期；内部不同设备可做 30-60s 缓存，避免频繁 GATT 访问。
  - 功能：
    - 枚举 BLE 设备接口（SetupAPI + GUID_BLUETOOTHLE_DEVICE_INTERFACE）。
    - 对每个设备打开句柄，查询 Service 0x180F 与 Characteristic 0x2A19；若存在则读取 battery_level。
    - 尝试解析设备友好名（PnP Name、Registry 设备名称）。
    - 组装 `batteries[]` 项（见上文字段），连接状态无法确定时标记 `unknown`。
  - 容错：任一设备读取失败不影响其他设备；所有异常捕获并按设备级别降级为 null/跳过。
  - 安全：仅本机读取，不做广播/主动配对；不持久化设备句柄；确保 Dispose 句柄。

- 注册：`src/SystemMonitor.Service/Services/Collectors/MetricsRegistry.cs` 中 `Register(new PeripheralBatteryCollector())`，顺序放在 `PowerCollector` 之后。

- RPC 输出：
  - `snapshot()`/`metrics` 增加 `peripherals: { batteries: [...] }`。
  - 保持与 `power` 模块并列（不嵌入 `power` 内），便于前端模块化展示。

—

## 4. 轮询与性能

- 周期：默认 10s（比核心系统指标低频），Burst 模式下最小 3s；避免持续占用无线带宽。
- 缓存：设备枚举列表缓存 60s；每设备电量值缓存 10-15s；
- 并发：串行读取设备（避免适配器堆栈拥堵），总体超时 3s。

—

## 5. 测试计划（`SystemMonitor.Tests`）

- 单元测试
  - 契约：`snapshot()` 返回 `peripherals`，`batteries` 为数组，字段名全 snake_case；
  - 兼容：无 BLE 适配器/无电池服务 → `batteries=[]`；
  - 范围：`battery_percent` 在 0..100 或 null；
  - 容错：单设备读取异常不影响整体返回。

- 端到端
  - `hello` → `start(['peripherals'])` → 每 10s 收到 `peripherals.batteries`；
  - 本机若有常见 BLE 键鼠/耳机，验证能读到电量值。

—

## 6. 前端改动（Tauri + Vue3）

- DTO：`frontend/src/api/dto.ts` 扩展 `SnapshotResult['peripherals']`；
- Store：`frontend/src/stores/metrics.ts` 透传 `peripherals`；
- 组件：
  - Debug/Snapshot 视图自动显示 `peripherals` JSON；
  - 新增 `PeripheralBatteryPanel.vue`（可后置），显示设备名、电量、连接状态、来源。
- 调试：沿用用户偏好，使用 `./scripts/dev.ps1` 启动本地联调。

—

## 7. 风险与回退

- WinRT GATT 在非打包环境不可用：采用 Win32 BthLEGatt API 路线。
- 设备不支持 0x180F：返回空或 null，后续尝试 HID Feature 报告作为补充。
- 多接收器/同名设备：以 `Device Instance Id` 唯一化；若不可得，则降级为地址/接口路径。
- 权限：普通用户上下文足够（读取设备接口/句柄），管理员非必需；Windows 服务可能因会话隔离导致无法访问蓝牙堆栈，建议该采集器在“有前端连接时启用”，并由服务以交互用户身份运行（当前阶段先实现为普通采集器，若确有权限问题再评估托管到 UI 侧的可能性）。

—

## 8. 实施步骤

1) 冻结字段（本文档）并征求确认；
2) 后端新增 `PeripheralBatteryCollector`（仅实现 BthLEGatt 路径 + 设备/值缓存）；
3) 注册采集器，扩展 `snapshot()`/推流；
4) 新增/完善单元与端到端测试；
5) 前端 DTO/显示与联调；
6) 记录已知限制，后续再评估 HID Feature 与厂商 SDK 补充路径。

—

## 9. 示例返回（示意）

```json
{
  "peripherals": {
    "batteries": [
      {
        "id": "BLE:FC-12-34-56-78-9A",
        "name": "MX Master 3S",
        "kind": "bt_le",
        "connection": "connected",
        "battery_percent": 87,
        "battery_state": "normal",
        "source": "gatt_0x180f_0x2a19",
        "last_update_ts": 1724460000000
      }
    ]
  }
}
```
