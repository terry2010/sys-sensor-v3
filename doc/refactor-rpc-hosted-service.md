# RpcHostedService 重构设计与迁移计划

本文档描述如何将当前“上帝类” `src/SystemMonitor.Service/Services/RpcHostedService.cs`（约 2k+ 行）拆解为职责单一、可测试、可扩展的模块体系。在保证对外 JSON-RPC 行为与现有测试不变的前提下，重构内部结构、理顺依赖方向并降低耦合度。

## 目标
- 提升可维护性与清晰度：按“管道宿主 / RPC 接口 / 采集器 / 采样器 / 硬件 / 互操作 / 工具方法 / DTO”分层。
- 便于扩展：新增指标或 RPC 方法时改动范围小、可预测。
- 线程安全与日志一致：统一锁与结构化日志字段，避免竞态与重复代码。
- 行为不变：接口签名、字段命名（snake_case）、错误码、推送与查询语义保持。

## 分层与依赖方向
仅允许自下而上依赖，禁止反向：

Interop → Helpers → Samplers/Hardware → Collectors → RpcServer → RpcHostedService

`HistoryStore` 仅供 `RpcServer` 使用（写入与查询）。

## 目录结构与职责
命名空间保持为 `SystemMonitor.Service.Services`，最大限度减少改动面。

- Services/Rpc/
  - RpcHostedService.cs（宿主/会话/ACL）：`ExecuteAsync()`、`CreateSecuredPipe()`、`HandleClientAsync()`
  - RpcServer.cs（JSON-RPC 实现）：`hello/snapshot/burst_subscribe/subscribe_metrics/set_config/start/stop/query_history`，会话状态、事件、历史缓冲、推送节流/突发
- Services/DTOs/
  - RpcDtos.cs：`HelloParams`、`SnapshotParams`、`BurstParams`、`SetConfigParams`、`StartParams`、`QueryHistoryParams`、`SubscribeMetricsParams`
- Services/Collectors/
  - IMetricsCollector.cs
  - CpuCollector.cs、MemoryCollector.cs、DiskCollector.cs、NetworkCollector.cs、GpuCollector.cs、SensorCollector.cs
  - MetricsRegistry.cs：集中注册采集器（替代当前静态 `s_collectors`）
- Services/Samplers/
  - CpuFrequency.cs、PerCoreFrequency.cs、PerCoreCounters.cs
  - KernelActivitySampler.cs、CpuLoadAverages.cs
  - TopProcSampler.cs、SystemCounters.cs
- Services/Hardware/
  - LhmSensors.cs（LibreHardwareMonitor 适配与一次性调试 dump 支持）
- Services/Interop/
  - Win32Interop.cs：`FILETIME`、`GetSystemTimes`、`MEMORYSTATUSEX`、`GlobalMemoryStatusEx`
- Services/Helpers/
  - SystemInfo.cs：`GetCpuUsagePercent()`、`GetCpuBreakdownPercent()`、`GetMemoryInfoMb()`、`TryGetMemoryStatus()`
  - （可选）JsonRpcHelpers.cs：`NotifyBridge()`、`EmitState()` 封装

## 可见性规范
- DTO：public（JSON 绑定需要）
- 采样器/硬件/收集器与注册表：internal（同命名空间可见）
- Interop/Helpers：internal static
- RpcServer：internal sealed

## 线程安全与性能
- 保留原有锁与 Lazy 单例：`_cpuLock`、`_cpuBkLock`、各 Sampler 内部锁、`RpcServer._lock/_subLock`。
- 统一节流：CPU/内核活动 200~1000ms；磁盘/网络 200ms；LHM 1.5s。
- 初始化“预热”：性能计数器/WMI 首次访问允许 0/异常回退。
- 历史环形缓冲 `MaxHistory=10000` 保持；SQLite 写入采用 try/catch，不阻塞推送。

## 日志与错误码
- 结构化字段统一：`conn_id`、`session_id`、`req`、`phase`、`interval_ms`、`expires_at`、`modules`、`count`。
- 错误码对齐：
  - 参数错误：`InvalidOperationException("invalid_params: ...")`
  - 未授权：`UnauthorizedAccessException("unauthorized")`
  - 不支持：`InvalidOperationException("not_supported: ...")`

## 测试策略
- 单元测试：按 Sampler/Collector 颗粒验证可用性与边界（节流可注入/放宽门槛）。
- 契约测试：保持 snake_case 与字段存在性（沿用 `src/SystemMonitor.Tests/ContractTests.cs`）。
- 端到端：沿用 `EndToEndTests.cs`，重点覆盖 burst、history 聚合边界、无数据回退。

## 迁移步骤（渐进，小步可回退）

Phase 1（最小风险，收益显著）
1. 抽离 DTO 到 `Services/DTOs/RpcDtos.cs`。
2. 抽离 Interop 到 `Services/Interop/Win32Interop.cs`。
3. 抽离 Helpers 到 `Services/Helpers/SystemInfo.cs`，并在 `RpcHostedService.cs` 顶部引入 `using static SystemMonitor.Service.Services.SystemInfo;`。
4. 从 `RpcHostedService.cs` 移除重复 DTO 与 Interop/Helper 定义，确保编译通过。

Phase 2（采样器与硬件）
- 抽离 `Samplers/*` 与 `Hardware/LhmSensors.cs`，保持 Lazy 单例与节流逻辑不变。

Phase 3（采集器与注册表）
- 抽离 `Collectors/*` 与 `MetricsRegistry.cs`，以注册表替代 `s_collectors` 静态列表。

Phase 4（RPC 接口拆出）
- 抽离 `RpcServer.cs`，`RpcHostedService.cs` 仅保留宿主/管道/会话 wiring。

Phase 5（一致性与回归）
- 统一日志/错误码/可见性，再跑契约与端到端测试，修正差异。

## 扩展建议（后续）
- DI 化采集器注册（`IServiceCollection`），便于按配置启停模块；当前先维持最小改动。
- 抽象 `IMetricsPushService`，将推流调度与 RPC 解耦。

---

## 执行计划
- 立即开始 Phase 1，并在每个 Phase 完成后产出简要变更摘要与测试结果。
- 所有重构过程中不修改对外 JSON-RPC 契约；若需新增字段/模块，将在独立 PR/提交中进行。

## 当前进度（2025-08-22 20:56）
- 【p1】已完成：精简 `RpcHostedService`（仅保留宿主/管道/会话 wiring），移除重复 DTO/Helper/Interop，新增启动阶段日志；解决编译与结构完整性问题。
- 【p2】已完成：`Samplers/*` 与 `Hardware/LhmSensors.cs` 已抽离；补强 `ISamplersProvider.TopProcSamplerRead()` 返回类型为 `object[]`，提升类型一致性与序列化清晰度；保持 Lazy 单例与节流逻辑不变。
- 【p3】已完成：`Collectors/*` 与 `MetricsRegistry.cs` 已落地，集中注册替代旧静态集合，提供只读访问。
- 【p4】已完成：`RpcServer.*` 已拆分为多个分部类文件，`RpcHostedService` 仅保留宿主/管道/会话 wiring 与会话生命周期管理。
- 【p5】已完成：统一日志关键字与错误码，修复 `query_history` 中 `memory` 字段空值判定，补充 `hello:` 入口日志；端到端与契约测试全部通过。

## 日志关键字段对齐清单（Phase 5）
- conn_id：连接标识（命名管道会话），来源 `RpcHostedService.HandleClientAsync()`。
- session_id：握手完成后的会话标识，来源 `RpcServer.hello()` 返回体。
- req：RPC 方法名或通知名，统一小写，例如 `hello`、`snapshot`、`notify.metrics`。
- phase：启动阶段与处理阶段标注，例如 `startup:history_store`、`startup:pipe_ready`、`accept`、`dispose`。
- interval_ms：推流/采样/聚合周期毫秒数（存在即记录）。
- expires_at：窗口抑制/订阅到期时间（存在即记录）。
- modules：启动/停止模块列表（如 `["cpu","memory"]`）。
- count：批量项数量（如历史查询返回条数、推送数组大小）。
- ts/seq：UTC 毫秒时间戳、推送序列号（桥接预热/推送路径记录）。

## DoD（完成定义，Phase 5）
- 所有端到端与契约测试通过（含 `hello`、`snapshot`、`query_history`、metrics push）。
- JSON 命名策略一致：服务端与测试均为 `SnakeCaseLower`；新增/返回对象均为 snake_case。
- `hello()` 必定产生日志，包含关键字 `hello:` 与结构化字段（app、proto、caps、conn）。
- `query_history` 返回 `memory` 字段时，仅在 `total` 与 `used` 同时存在才返回对象；否则返回 `null`，避免序列化异常。
- 启动阶段日志完整：`startup:history_store` → `startup:pipe_ready` → `accept` → `dispose` 等关键节点可检索。
- 桥接连接（`metrics_stream` 能力）在握手后触发预热通知（含 `ts`/`seq`），并默认启动 `cpu/mem` 模块。
