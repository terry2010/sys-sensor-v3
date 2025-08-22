# 磁盘与存储指标开发计划（Windows）

本文件定义“磁盘与存储（Disks & Storage）”模块的指标集合、字段命名（snake_case）、单位、数据来源与回退策略、采样与聚合、前后端契约、测试策略与验收标准。完成后需与 `doc/istat-menus-metrics.md` 的“4. 磁盘与存储”逐条对照，确保无遗漏（平台不适用项返回 null 并注明）。

---

## 1. 指标分组与字段定义

说明：Windows 环境下尽量对齐 iStat Menus 语义；macOS/APFS 专属项在 Windows 下标记为 null 或映射为等价概念（例如 BitLocker、VSS、pagefile/hiberfil）。

### 1.1 容量/卷信息（Capacity & Volumes）
- per_physical_disk[]（每物理磁盘：Win32_DiskDrive / MSFT_PhysicalDisk）
  - id: string  // 唯一标识（DeviceId/PhysicalDiskNumber/NVMe Serial 合成）
  - model: string | null
  - serial: string | null
  - firmware: string | null
  - size_total_bytes: number | null
  - partitions: number | null
  - media_type: string | null  // ssd/hdd/unknown（MSFT_PhysicalDisk.MediaType）
  - spindle_speed_rpm: number | null  // HDD 转速（若可读）
  - interface_type: string | null  // nvme/sata/usb/thunderbolt/scsi（推断）
  - trim_supported: boolean | null

- per_volume[]（每逻辑卷/挂载点：Win32_LogicalDisk & GetDriveType）
  - id: string  // 卷 GUID 或驱动器号（C:）
  - mount_point: string
  - fs_type: string | null
  - size_total_bytes: number | null
  - size_used_bytes: number | null
  - size_free_bytes: number | null
  - read_only: boolean | null
  - bitlocker_encryption: string | null  // on/off/unknown（若可读）
  - is_removable: boolean | null

- totals（聚合）
  - total_bytes: number | null
  - used_bytes: number | null
  - free_bytes: number | null

- 平台差异映射
  - purgeable_space_bytes: null  // APFS 专属，Windows 无直接等价
  - apfs_local_snapshots_count/bytes: null  // Windows 可选映射 VSS，将在后续“增强”阶段评估
  - vm_swapfiles_bytes: number | null  // 映射为 pagefile.sys + hiberfil.sys 文件大小之和（若可读）

### 1.2 实时 I/O 与活动（Real‑time I/O & Activity）
- io_totals（所有磁盘总览：PhysicalDisk\_Total）
  - read_bytes_per_sec: number
  - write_bytes_per_sec: number
  - read_iops: number | null  // Disk Reads/sec（若计数器可用）
  - write_iops: number | null // Disk Writes/sec
  - busy_percent: number | null  // 100 - %Idle 或 %Disk Time（归一化）
  - queue_length: number | null  // Current Disk Queue Length
  - avg_read_latency_ms: number | null  // Avg. Disk sec/Read * 1000
  - avg_write_latency_ms: number | null // Avg. Disk sec/Write * 1000

- per_physical_disk_io[]（每物理盘实例：PhysicalDisk(<instance>)）
  - disk_id: string
  - read_bytes_per_sec: number
  - write_bytes_per_sec: number
  - read_iops: number | null
  - write_iops: number | null
  - busy_percent: number | null
  - queue_length: number | null
  - avg_read_latency_ms: number | null
  - avg_write_latency_ms: number | null

- per_volume_io[]（每卷：LogicalDisk(<instance>)）
  - volume_id: string
  - read_bytes_per_sec: number | null
  - write_bytes_per_sec: number | null
  - read_iops: number | null
  - write_iops: number | null
  - free_percent: number | null  // 便于前端展示

- top_processes_by_disk[]（按磁盘 I/O 的进程榜，读/写分列，可选增强）
  - pid: number
  - name: string
  - read_bytes_per_sec: number
  - write_bytes_per_sec: number

### 1.3 设备/接口信息（Device & Interface）
- per_physical_disk_info[]（补充静态信息）
  - bus_type: string | null  // NVMe/SATA/USB/Thunderbolt/PCIe/SAS
  - negotiated_link_speed: string | null  // 若可读（NVMe: PCIe 链路速率/宽度；SATA: 3/6Gbps）
  - removable: boolean | null
  - eject_capable: boolean | null

### 1.4 健康/温度（Health & S.M.A.R.T./NVMe）
- smart_health[]（若可读，来自 NVMe Log / ATA SMART，或 LibreHardwareMonitor）
  - disk_id: string
  - overall_health: string | null  // ok/warning/fail/unknown
  - temperature_c: number | null  // 设备/控制器温度
  - power_on_hours: number | null
  - reallocated_sector_count: number | null
  - pending_sector_count: number | null
  - udma_crc_error_count: number | null
  - nvme_percentage_used: number | null
  - nvme_data_units_read: number | null
  - nvme_data_units_written: number | null
  - nvme_controller_busy_time_min: number | null
  - unsafe_shutdowns: number | null
  - thermal_throttle_events: number | null

---

## 2. 数据来源与回退策略（Windows）

- Performance Counters（优先，用于实时 I/O）
  - Category: "PhysicalDisk" / "LogicalDisk"
  - Counters: "Disk Read Bytes/sec"、"Disk Write Bytes/sec"、"Disk Reads/sec"、"Disk Writes/sec"、"% Idle Time"、"Avg. Disk sec/Read"、"Avg. Disk sec/Write"、"Current Disk Queue Length"、"Avg. Disk Queue Length" 等
  - 采样节流：>= 200ms（与现有 `DiskCounters`/`NetCounters` 一致）
  - 回退：若计数器不可用，返回 0 或 null，同时记录 Debug 日志一次，避免刷屏

- WMI/CIM（容量、卷、设备静态信息）
  - Win32_LogicalDisk（容量/FS/驱动器类型/只读）
  - Win32_DiskDrive（型号/序列/固件/接口/大小/分区）
  - MSFT_PhysicalDisk（媒体类型/转速/BusType）
  - 可选：BitLocker WMI（加密状态，若有权限与 API）

- Windows Storage API / IOCTL（健康/NVMe/SMART/链路速率）
  - IOCTL_STORAGE_QUERY_PROPERTY（标准存储属性）
  - NVMe Log Page（需要管理员/驱动支持；失败则回退）
  - ATA SMART（同上）
  - 回退：若底层不可用，改用 LibreHardwareMonitor 的传感器；再不行则 null

- LibreHardwareMonitor（温度与部分 SMART 读数）
  - 通过 `SensorsProvider` 统一读取，按磁盘匹配（model/serial 上尽量做映射）

- VSS / 休眠与分页文件
  - VSS 映射 APFS 快照：默认不启用，作为增强项评估
  - pagefile.sys / hiberfil.sys 大小：通过文件属性或 WMI 读取，汇总为 `vm_swapfiles_bytes`

---

## 3. 采样、聚合与命名

- 命名：统一 snake_case；外部契约以英文、单位注释在本文件
- 粒度：
  - 实时 I/O：总览 + per_physical_disk_io + per_volume_io
  - 容量：per_volume + totals
  - 设备/健康：per_physical_disk + per_physical_disk_info + smart_health
- 节流：实时计数器 200–500ms；静态信息（设备/固件/加密）缓存 30–60s 或按变更事件刷新
- 历史：`HistoryStore` 仅保留总览必要指标（读写速率、忙碌度），避免写放大；其他在 UI 端按需拉取

---

## 4. 后端实现计划（分阶段）

- 阶段 A（基础实时 I/O 与总览）
  1) 扩展 `DiskCounters`：加入 Reads/sec、Writes/sec、%Idle、Avg. Disk sec/*、Avg/Current Queue Length；提供 per‑instance 读取
  2) 新增 `LogicalDiskCounters`：卷级 I/O 与容量/空闲（或容量另由 WMI）
  3) 扩展 `DiskCollector`：输出 io_totals / per_physical_disk_io / per_volume_io / totals

- 阶段 B（容量与卷/设备静态信息）
  1) 新增 `StorageQuery`（WMI/CIM）：`per_volume` & `per_physical_disk`
  2) BitLocker 状态（可选，读取失败则 null）

- 阶段 C（设备/接口信息）
  1) MSFT_PhysicalDisk.BusType/MediaType/SpindleSpeed
  2) IOCTL_STORAGE_QUERY_PROPERTY（NVMe/SATA 链路信息）

- 阶段 D（健康/温度）
  1) 先接入 LibreHardwareMonitor 的磁盘温度
  2) 再尝试 NVMe Log / SMART 属性（权限/驱动受限时回退）

- 阶段 E（进程榜）
  1) 采样每进程 I/O 增量，计算 read/write bytes/sec
  2) 输出 top N（读/写分列）

---

## 5. 测试计划

- 单元测试（`SystemMonitor.Tests`）
  - __[计数器与节流]__ 模拟 PerfCounter 不可用/异常 → 返回零/空且无抖动
  - __[聚合准确性]__ per‑instance 汇总 == totals（允许微小误差）
  - __[命名/契约]__ 对照字段名与 null 语义（平台不适用）

- 集成测试
  - __[WMI/CIM]__ `per_volume`/`per_physical_disk` 能成功返回，字段类型正确
  - __[LHM]__ 传感器可读时温度字段非空；不可读时为 null

- 端到端（E2E）
  - 通过 JSON‑RPC 订阅 `metrics`，校验磁盘模块字段完整性与数值范围
  - 启动 `scripts/dev.ps1`，在 UI 中显示读写速率曲线、卷容量

- 合约与架构测试
  - JSON Schema/契约测试：新增/变更字段需更新并校验
  - 性能回归：高 I/O 压力下 CPU 占用可控，无内存泄漏

---

## 6. 验收标准

- 字段覆盖达到本计划“1. 指标分组与字段定义”的全部项；平台不适用项返回 null 并在日志与文档中注明
- 在 I/O 持续变化环境下，速率与 IOPS/延迟曲线稳定，数值无明显抖动或异常尖刺
- 静态信息与容量在设备变更/卷挂载变化后能在 60s 内刷新
- E2E 演示：主窗口展示磁盘读/写速率与卷容量；日志无异常堆栈

---

## 7. 与 iStat Menus 清单对照（无遗漏声明）

- 覆盖项：
  - 容量/卷信息：总量/已用/可用、文件系统类型、挂载点、只读/移除介质、加密（映射 BitLocker）
  - 实时 I/O：读/写速度、读/写 IOPS、忙碌比例、队列深度、平均读/写延迟、顶部进程（增强）
  - 设备/接口：型号/固件/序列、介质类型、接口类型、转速、TRIM 支持、外接设备/弹出能力、链路速率
  - 健康/温度：SMART 总体健康、常见 SATA 属性、NVMe 属性、设备/控制器温度与热节流状态
- 平台差异（保留字段但在 Windows 返回 null，或后续增强）：
  - APFS 可清理空间（purgeable_space）、APFS 本地快照（local snapshots）
  - VM/Swapfiles：Windows 以 pagefile/hiberfil 映射

---

## 8. 开发工作流（逐指标落实）

1) 在 `doc/todo-2025-08-22.md` 补充本模块条目（内存已标记完成），为每批指标打勾并写明来源/回退
2) 修改后端（按阶段 A→E 逐步合入）
3) 本地编译直到通过
4) 修改/新增测试并反复运行直到通过（单元/集成/契约/E2E）
5) 前端（Tauri）主进程临时打印磁盘指标并在面板展示
6) 使用 `scripts/dev.ps1` 启动联调，确认打印值合理
7) 移除临时打印
8) 重读 `doc/istat-menus-metrics.md`，选择下一模块

---

## 9. 风险与回退

- 某些计数器或 WMI 类在用户环境不可用：值置 0 或 null，并降级日志
- NVMe/SMART 读取权限不足：优先 LHM 传感器回退；仍失败则置 null
- 高频采样导致性能问题：加大节流与结果缓存，或仅在订阅相应字段时启用
