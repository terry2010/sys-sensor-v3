# 指标刷新性能优化设计与实施计划

更新时间：2025-08-23 23:14
适用平台：Windows 10（Tauri 前端 + .NET 后端）

## 1. 目标与成功标准
- 更快的首帧可见：应用启动后 1s 内收到首个 `metrics`，网络与顶部健康区优先显示。
- 更高的刷新流畅度：常态刷新间隔 500–1000ms；突发期 200ms；无明显卡顿。
- 资源可控：CPU 开销小于 3–5%，磁盘/网络等 I/O 调用有界并发，不引发系统抖动。

## 2. 现状与瓶颈
- 后端默认推送间隔 `_baseIntervalMs = 1000ms`（`src/SystemMonitor.Service/Services/RpcServer.Config.cs`）。
- 推送循环在 `RpcHostedService.HandleClientAsync()` 内同步串行遍历 `MetricsRegistry.Collectors` 调用 `Collect()`（`src/.../RpcHostedService.cs` 187–197）。
- `DiskCollector` 体量与逻辑复杂（≈738 行），包含 SMART/IOCTL/NVMe Identify/错误日志/容量等“重 I/O”，易阻塞整轮采集（`src/.../Collectors/DiskCollector.cs`）。
- `NetCounters` 轻量且带 200ms 缓存（`src/.../Collectors/NetCounters.cs`），一般不是瓶颈。
- 前端此前存在 2s 启动延迟与保守订阅策略，已修复（立即启动 + 突发订阅）。

## 3. 已实施改动（快速收益）
- 调整采集顺序：`network` 在 `disk` 之前，保障网络/CPU/内存首帧优先（`src/.../Collectors/MetricsRegistry.cs`）。
- 采集耗时阈值日志：
  - 单采集器 >200ms 输出 `collector slow: {Name} took {Elapsed}ms`。
  - 整轮采集耗时 > 当前推送间隔输出 `metrics collect slow: total {Total}ms exceeds interval {Interval}ms`（`src/.../RpcHostedService.cs`）。
- 前端：移除 2s 延迟、握手后突发订阅 200ms/5s、默认启动包含 `network`（`frontend/src/App.vue`、后端 `RpcServer.Handshake.cs`）。

## 4. 规划中的系统性优化（分阶段）

### Phase 1（诊断 + 保障首帧）
- 已完成：顺序前移 + 阈值日志 + 前端突发。
- 验证：通过 `collector slow` 与 `metrics collect slow` 日志确认是否为 `disk` 阻塞；观察 UI 首帧时延与突发期刷新。

### Phase 2（采集减阻：磁盘快/慢路径）
- 将 `DiskCollector.Collect()` 拆分为：
  - 快路径：每轮仅聚合速率/繁忙度（`DiskCounters`/`LogicalDiskCounters`），避免任何 SMART/Identify/容量/错误日志重查询。
  - 慢路径后台任务：10–30s 定时异步刷新 SMART、Identify、容量、错误日志等信息，落入内存缓存；`Collect()` 直接读取缓存。
- 技术点：
  - 线程安全缓存对象 + 时间戳；
  - 环境变量开关以完全禁用重查询（生产应默认开启慢路径、关闭快路径中的重 I/O）。

### Phase 3（采集并行化与有界并发）
- 推送循环对采集器使用有界并发（并发度 2–3）：
  - 仅将 I/O 密集型（disk、network）并行化；CPU/内存仍同步。
  - 为每个采集器设置软超时（150–300ms），超时回退至上次缓存或跳过，确保按时推送。
- 关键伪代码：
```csharp
var enabled = rpcServer.GetEnabledModules();
var tasks = collectors.Select(c => RunWithTimeoutAsync(() => c.Collect(), 300));
var results = await Task.WhenAll(tasks);
```
- 风险控制：`SemaphoreSlim` 限流；捕获异常并记录 `collector slow/timeout`。

### Phase 4（分频采集与策略化刷新）
- 为模块设置最小采样周期：
  - network/cpu：200–500ms；
  - disk 速率：500–1000ms；
  - SMART/Identify/容量/错误日志：10–60s。
- 在各 `Collector` 内部判定距离上次刷新是否到期，未到期则返回缓存。

### Phase 5（突发阶段策略）
- 启动后 3–5s 的突发期：
  - 降频或暂缓 `disk` 快路径（仅在 UI 首帧稳定后再恢复）。
  - 或仅推送 `cpu/memory/network`，待突发结束后再启用 `disk`。

## 5. 资源与风险评估
- 有界并发 + 缓存可避免 CPU 飙升。
- 重 I/O 后台化与分频可显著降低系统压力与卡顿几率。
- 需关注的风险：
  - 后台任务与快路径的线程安全；
  - 缓存一致性与 JSON 字段稳定性；
  - 少数设备 IOCTL 调用不可预期耗时，必须有超时与降级。

## 6. 全局配置与前端交互参数

为满足“统一配置、可由前端设置”的诉求，新增四项全局设置，均通过 `set_config` RPC 下发：

- __采集间隔（全局）__：`base_interval_ms: number`  
  描述：主推送周期的基础间隔。允许前端动态下发，服务端做下限保护（≥100ms）。

- __采集并发度（全局）__：`max_concurrency: number`  
  描述：异步采集的并发上限（有界并发）。前端可设 1–8，服务端限幅至 [1, 8]，默认 2。

- __启用模块（全局）__：`enabled_modules: string[]`  
  描述：启用参与推送的指标模块，默认“全部注册模块”。如果传空数组表示“全部”，若为 null/未提供则保持现状。

- __同步豁免模块（全局）__：`sync_exempt_modules: string[]`  
  描述：这些模块在推送循环中使用同步直采（通常是 CPU/Memory 这类轻量指标），不在此列表的模块走异步并发采集。默认 `["cpu", "memory"]`。

### set_config 请求 JSON 示例

```json
{
  "base_interval_ms": 500,
  "max_concurrency": 3,
  "enabled_modules": ["cpu", "memory", "network", "disk"],
  "sync_exempt_modules": ["cpu", "memory"],
  "persist": false
}
```

### 服务端处理要点
- `base_interval_ms`：下限保护 100ms；写入 `_baseIntervalMs`。
- `max_concurrency`：范围保护 1–8；写入 `_maxConcurrency`。
- `enabled_modules`：为空数组或未提供时表示“全部”；否则与 `MetricsRegistry.Collectors` 求交集后写入 `_enabledModules`。
- `sync_exempt_modules`：与已知模块求交集后写入 `_syncExemptModules`，默认包含 `cpu`、`memory`。
- 响应中返回当前生效配置的摘要，前端据此回显。

## 7. 推送循环改造（与全局设置对齐）

- 从 `RpcServer` 读取：`GetCurrentIntervalMs()`、`GetEnabledModules()`、`GetMaxConcurrency()`、`GetSyncExemptModules()`。
- 将采集器分为：
  - 同步豁免集（sync）：顺序同步 `Collect()`；
  - 异步并发集（async）：使用 `SemaphoreSlim(concurrency)` 限流并发执行，汇聚结果后入 payload。
- 保留耗时阈值日志：
  - 单采集器 >200ms 记录 `collector slow`；
  - 整轮采集 > interval 记录 `metrics collect slow`。

## 8. 度量与回归验证（更新）
- set_config 生效确认：服务端日志打印新值；响应体包含 `base_interval_ms`、有效模块列表、并发度、同步豁免集。
- 观察并发度变化对 CPU 的影响（目标 <3–5%）；确认 UI 刷新无卡顿。

## 9. 度量与回归验证（历史章节编号顺延）
- 日志度量：
  - 单采集器耗时（p50/p95/p99 可后续聚合）。
  - 整轮采集耗时 vs 当前推送间隔。
- UI 观测：首帧时间、突发期刷新体验、稳定期刷新间隔。
- 自动化：添加简单基准脚本观测 1 分钟内 `metrics` 事件数与平均延迟。

## 10. 回滚预案
- 提供配置开关（环境变量或 `set_config`）一键关闭并行与后台重 I/O，仅保留快路径与 1s 刷新，快速回退至稳定基线。

## 11. 实施路线图与里程碑
- M1（已完成）：采集顺序 + 阈值日志 + 前端突发（1 天）。
- M2：`DiskCollector` 快/慢路径与后台缓存（1–2 天）。
- M3：推送有界并发 + 超时回退（0.5–1 天）。
- M4：分频采集与配置化（0.5 天）。
- M5：验证与回归、文档固化（0.5 天）。

## 12. 相关代码位置（引用）
- 推送循环：`src/SystemMonitor.Service/Services/RpcHostedService.cs`
- 采集注册：`src/SystemMonitor.Service/Services/Collectors/MetricsRegistry.cs`
- 网络采集：`src/SystemMonitor.Service/Services/Collectors/NetCounters.cs`
- 磁盘采集：`src/SystemMonitor.Service/Services/Collectors/DiskCollector.cs`
- 前端事件桥/突发订阅：`frontend/src/App.vue`、`frontend/src/api/rpc.tauri.ts`
- 握手与自动启动：`src/SystemMonitor.Service/Services/RpcServer.Handshake.cs`

## 13. 后续工作清单
- 实施 Phase 2：磁盘快/慢路径与后台缓存。
- 实施 Phase 3：有界并发与超时回退。
- 实施 Phase 4：模块分频采集与缓存时间窗。
- 增加度量聚合（统计采集耗时分位数，输出到日志）。
- （可选）默认基础间隔从 1000ms 下调至 500ms，通过 `set_config` 配置并持久化。
