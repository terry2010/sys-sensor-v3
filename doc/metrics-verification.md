# 指标覆盖核验（逐项汇总）

说明：本文件按 `doc/istat-menus-metrics.md` 的顺序逐项核对。仅基于代码阅读与前端源码确认，不运行程序。

---

## 1. CPU（中央处理器）

### 1.1 使用率：用户（User）/系统（System）/空闲（Idle）/Nice
- 覆盖结论：
  - 用户/系统/空闲 已实现并在首页展示；Nice 未实现（未采集/未展示）。
- 后端实现：
  - 文件：`src/SystemMonitor.Service/Services/Collectors/CpuCollector.cs`
  - 字段：
    - `usage_percent`
    - `user_percent`
    - `system_percent`
    - `idle_percent`
  - 证据：`Collect()` 返回匿名对象，含以上字段；未发现 `nice_percent` 或同义键。
- 前端展示：
  - 页面容器：`frontend/src/App.vue` 引入 `CpuPanel`，作为首页卡片展示。
  - 组件：`frontend/src/components/CpuPanel.vue`
    - 顶部汇总：`usage`（使用 `cpu?.usage_percent`）
    - 条形分布：`user_percent`、`system_percent`、`idle_percent`
    - 文本汇总：`user/system/idle` 三项百分比
  - 数据路径：`frontend/src/stores/metrics.ts` 中 `MetricPoint.cpu` 最小定义包含 `usage_percent`，运行时通过事件桥写入 `metrics.latest.cpu.*`，`CpuPanel.vue` 以 `metrics.latest.cpu` 读取。
- 与清单对照：
  - User/System/Idle：匹配 ✅
  - Nice：未实现（后端无 `nice_percent`，前端无展示）❌
- 备注/建议：如需对齐 iStat Menus 的四项分解，需要在 `CpuCollector` 增加 nice 时间比例采样，并在 `CpuPanel.vue` 增加 `nice_percent` 的展示（条形或文本），同时在 `MetricPoint` 类型中扩展定义。

### 1.2 平均负载（Load Average）：1/5/15 分钟
- 覆盖结论：
  - 已实现并在首页展示。
- 后端实现：
  - 文件：`src/SystemMonitor.Service/Services/Collectors/CpuCollector.cs`
  - 字段：`load_avg_1m`、`load_avg_5m`、`load_avg_15m`
  - 证据：`Collect()` 返回包含 `(l1, l5, l15)`，并映射为上述字段。
- 前端展示：
  - 组件：`frontend/src/components/CpuPanel.vue`
  - 行：`<span class="k">load 1/5/15</span>` 对应 `cpu?.load_avg_1m / 5m / 15m`
  - 数据路径：通过 `metrics.latest.cpu.*` 读取。
- 备注/建议：
  - 当前以 `pct()` 显示并附加 `%` 符号，而负载平均值通常为无量纲（不应加百分号）。建议将显示格式从百分比改为数值（如保留 2 位小数），以避免语义歧义。

### 1.3 进程数、线程数
- 覆盖结论：
  - 已实现并在首页展示。
- 后端实现：
  - 文件：`src/SystemMonitor.Service/Services/Collectors/CpuCollector.cs`
  - 字段：`process_count`、`thread_count`
  - 证据：`Collect()` 读取 `SamplersProvider.Current.SystemCountersReadProcThread()` 并返回对应字段。
- 前端展示：
  - 组件：`frontend/src/components/CpuPanel.vue`
  - 行：`<span class="k">proc/thread</span>` 对应 `cpu?.process_count` 与 `cpu?.thread_count`
  - 数据路径：通过 `metrics.latest.cpu.*` 读取。

### 1.4 运行时间（Uptime）
- 覆盖结论：
  - 已实现并在首页展示。
- 后端实现：
  - 文件：`src/SystemMonitor.Service/Services/Collectors/CpuCollector.cs`
  - 字段：`uptime_sec`
  - 证据：`Collect()` 中以 `Environment.TickCount64` 计算秒并返回。
- 前端展示：
  - 组件：`frontend/src/components/CpuPanel.vue`
  - 行：`<span class="k">uptime</span>` 对应 `cpu?.uptime_sec`，并使用 `uptime()` 将秒格式化为 `HH:MM:SS`。
  - 数据路径：通过 `metrics.latest.cpu.*` 读取。

### 1.5 每核/分组
- 覆盖结论：
  - 每核使用率与（可选）每核 MHz 已实现并在首页展示；P/E 核分组未实现。
- 后端实现：
  - 字段：`per_core`（数组，按核心顺序的使用率）、`per_core_mhz`（数组，可为空）。
- 前端展示：
  - 组件：`frontend/src/components/CpuPanel.vue`
  - 区块：`per-core (usage / MHz)` 列表，使用 `cpu.per_core` 与（若存在）`cpu.per_core_mhz[idx]`。
- 与清单对照：
  - 每核使用率：匹配 ✅
  - Apple Silicon P/E 核分组：未实现（无分组字段/展示）❌

### 1.6 频率/时钟（当前/最低/最高/Bus/倍频）
- 覆盖结论：
  - 已实现（部分字段条件显示）。
- 后端实现：
  - 字段：`current_mhz`、`max_mhz`、`min_mhz`、`bus_mhz`、`multiplier`。
- 前端展示：
  - 组件：`frontend/src/components/CpuPanel.vue`
  - 行：`freq`（current/max），`min freq`（可选），`bus/mult`（可选）。

### 1.7 热/功（温度与功率，与“传感器”重叠）
- 覆盖结论：
  - 已实现并在首页展示（CPU 面板内聚合）。
- 后端实现：
  - 温度字段：`package_temp_c`、`cores_temp_c`、`cpu_die_temp_c`、`cpu_proximity_temp_c`。
  - 功率字段：`package_power_w`、`cpu_power_ia_w`、`cpu_power_gt_w`、`cpu_power_uncore_w`、`cpu_power_dram_w`。
- 前端展示：
  - 组件：`frontend/src/components/CpuPanel.vue`（相应可选行均已实现）。

### 1.8 内核活动（若可用）
- 覆盖结论：
  - 已实现并在首页展示（条件渲染）。
- 后端实现：
  - 字段：`context_switches_per_sec`、`syscalls_per_sec`、`interrupts_per_sec`。
- 前端展示：
  - 行：`ctx/sys/irq`，以每秒速率格式化显示。

### 1.9 顶部占用进程（按 CPU%）
- 覆盖结论：
  - 已实现并在首页展示。
- 后端实现：
  - 字段：`top_processes`（数组，含 `name`/`pid`/`cpu_percent`）。
- 前端展示：
  - 区块：`top processes` 列表，按条目显示名称、CPU%、PID。

---

## 2. GPU（图形处理器）

### 2.1 使用率（Overall 与按适配器）
- 覆盖结论：
  - 已实现并在首页展示。
- 后端实现：
  - 文件：`src/SystemMonitor.Service/Services/Collectors/GpuCollector.cs`
  - 字段：
    - 适配器对象数组：`adapters`（通过 `Collect()` 组装返回）。
    - 每个适配器：`usage_percent`、`usage_by_engine_percent`（可含 `3d`/`compute`/`copy`/`video_decode`/`video_encode`）。
    - 其它相关：DXGI 名称/预算、显存用量、温度/时钟/功耗等（此处只就使用率核验）。
  - 证据：`GpuCollector.Collect()` 聚合 PerformanceCounter/WMI 的 GPU Engine 实例，按适配器汇总总使用率 `Total` 并映射为 `usage_percent`，各引擎类型映射为 `usage_by_engine_percent`。
- 前端展示：
  - 组件：`frontend/src/components/GpuPanel.vue`
  - 顶部：`GPU Usage` 使用 `overallGpuUsage`，来源为活跃适配器的 `usage_percent`（否则取全局最大值）。
  - 表格：对每个 `adapters[i]`，显示 `usage_percent` 以及细分 `3D%/Compute%/Copy%/VDec%/VEnc%`（取自 `usage_by_engine_percent`）。
  - 数据路径：`metrics.latest.gpu.adapters[*].usage_percent` 与 `metrics.latest.gpu.adapters[*].usage_by_engine_percent`。
- 与清单对照：
  - GPU 总体/单适配器使用率：匹配 ✅
  - 引擎细分使用率：已实现（若底层计数器可用时显示）✅

---

后续将按清单顺序继续补充各指标核验结果。
