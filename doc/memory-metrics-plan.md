# 内存指标开发计划（Windows）

本文件记录内存（Memory）指标的字段集合、命名规范、数据来源/回退策略、采样与展示要求，以及逐项开发的工作流检查清单。

---

## 1. 字段集合与命名（snake_case，单位注明）

必选（不保留旧字段 total/used，直接采用以下新字段）：
- total_mb: number
- used_mb: number
- available_mb: number
- percent_used: number [0,100]
- cached_mb: number  // 文件系统缓存
- commit_limit_mb: number
- commit_used_mb: number
- commit_percent: number [0,100]
- swap_total_mb: number
- swap_used_mb: number

可选增强（若不可用则返回 null）：
- pages_in_per_sec: number | null
- pages_out_per_sec: number | null
- page_faults_per_sec: number | null
- compressed_bytes_mb: number | null // Win10+ Memory\Compressed Bytes
- pool_paged_mb: number | null
- pool_nonpaged_mb: number | null
- standby_cache_mb: number | null // Standby Cache 合计（见来源）
- working_set_total_mb: number | null // 系统视角工作集总量（估算，见来源）
- memory_pressure_percent: number | null // max(percent_used, commit_percent)
- memory_pressure_level: string | null // green|yellow|red（阈值：<70 green, 70-90 yellow, >90 red）

说明：
- 数值单位一律为 MB（或每秒计数为“每秒”）。 
- 允许 null 表示数据源在当前系统/版本不可用或读取失败（需记录日志为 Debug 级）。

---

## 2. 数据来源与回退策略

- total_mb / available_mb / used_mb / percent_used
  - 主来源：Win32 API GlobalMemoryStatusEx（已有封装 `TryGetMemoryStatus`）
  - 计算：used = total - available，percent_used = used/total*100

- cached_mb（文件系统缓存）
  - 主来源：PerfCounter "Memory\\Cache Bytes"
  - 回退：null

- commit_limit_mb / commit_used_mb / commit_percent
  - 主来源：PerfCounter "Memory\\Committed Bytes" 与 "Memory\\Commit Limit"
  - 计算：commit_percent = CommittedBytes/CommitLimit*100
  - 回退：null

- swap_total_mb / swap_used_mb（Page File）
  - 主来源：WMI `Win32_PageFileUsage`，聚合所有实例：
    - total = sum(AllocatedBaseSize)
    - used = sum(CurrentUsage)
  - 回退：
    - PerfCounter "Paging File(_Total)\\% Usage" 获取百分比；若能同时估出容量，则 used=percent*total，否则置 null

- pages_in_per_sec / pages_out_per_sec / page_faults_per_sec
  - 主来源：PerfCounter：
    - "Memory\\Page Reads/sec"
    - "Memory\\Page Writes/sec"
    - "Memory\\Page Faults/sec"
  - 回退：null（在计数器不存在或权限不足时）

- compressed_bytes_mb
  - 主来源：PerfCounter "Memory\\Compressed Bytes"（Win10+）
  - 回退：null

- pool_paged_mb / pool_nonpaged_mb
  - 主来源：PerfCounter "Memory\\Pool Paged Bytes"、"Memory\\Pool Nonpaged Bytes"
  - 回退：null

- standby_cache_mb
  - 主来源：PerfCounter Standby Cache 相关计数器求和（常见：
    - "Memory\\Standby Cache Normal Priority Bytes"
    - "Memory\\Standby Cache Reserve Bytes"
    - 以及（若存在）"Memory\\Standby Cache Core Bytes" 等
  ) 将能获取到的 Standby Cache 字段累加为总量
  - 回退：null

- working_set_total_mb（系统视角）
  - 方案A：PerfCounter "Memory\\System Cache Resident Bytes" + 其他池等估算（波动较大，不推荐单独暴露）
  - 方案B（更稳妥）：WMI `Win32_PerfRawData_PerfOS_Memory` 的 Working Set 字段（可用性需实测）；若不可得则 null
  - 回退：null

- memory_pressure_percent / memory_pressure_level
  - 计算：pressure = max(percent_used, commit_percent)
  - level：<70 green；70-90 yellow；>90 red

所有 PerfCounter 读取与 WMI 查询均需：
- 初始化一次（缓存在 MemoryCounters 类），
- 读取失败不抛异常，返回 null 并记录 Debug 日志，
- 限制读取频率（如 200ms 内重复调用返回上次缓存）。

---

## 3. 后端实现改动（概要）

- 新增 `Services/Samplers/MemoryCounters.cs`：封装上述 PerfCounter/WMI 读取与节流缓存。
- 扩展 `Services/Helpers/SystemInfo.cs`：新增 `GetMemoryDetail()`，整合 GlobalMemoryStatusEx + MemoryCounters 信息为 1 个对象。
- 更新 `Services/Collectors/MemoryCollector.cs`：返回第 1 节中的字段集合（不保留旧字段）。
- 若前端/测试引用旧字段，需要同步调整契约。

---

## 4. 测试计划（`src/SystemMonitor.Tests`）

- 契约测试：
  - 字段齐全性与命名（snake_case），单位与范围（百分比在 0..100）。
  - 可选字段允许为 null；必选字段为非负。
- 采样稳定性：
  - 连续两次采样非负、无异常抖动（速率类允许 0）。
- 回退路径：
  - 人为禁用/模拟无法创建某 PerfCounter/WMI 时，字段为 null 或回退逻辑生效。

---

## 5. 前端与调试

- 在 `frontend/src/components/MemoryPanel.vue` 展示分组：容量/虚拟/缓存/交换/动态/压力。
- Tauri 主进程开发期临时打印本模块字段，联调完成后注释。

---

## 6. 工作流程（逐指标落实）

1) 在 `doc/todo-2025-08-22.md` 补充并勾选内存指标的字段与来源说明。
2) 修改后端：扩展 `SystemInfo.GetMemoryDetail()` 与 `MemoryCollector.Collect()`（保持 snake_case）。
3) 反复编译，直到通过。
4) 修改/新增测试并反复运行，直到通过。
5) 前端（Tauri）临时在主进程打印该指标并展示。
6) 使用 `scripts/dev.ps1` 启动联调，确认打印值合理。
7) 注释掉主进程打印。
8) 重读 `doc/istat-menus-metrics.md`，选择下一项。

---

## 7. 验收标准

- 后端快照与推流均包含第 1 节列出的字段；可选字段在不可用时为 null。
- 单元/集成测试全部通过。
- 前端正确显示；Tauri 主进程联调打印符合预期后移除调试打印。
- 文档与 TODO 勾选完成。
