# 实现计划（草案）

## 1. 依赖关系图（文字）
- `Collectors/*` → `MetricsCollectionService` → `RpcService/DataStorageService`
- `RpcService` ↔ `StreamJsonRpc/NamedPipe`
- `SqliteStore` ↔ `Aggregator`
- 前端 `useJsonRpc` → `jsonrpc.ts` → Rust 插件 `pipe`

## 2. 开发顺序（含理由）
1) 契约/模型冻结（避免返工）
2) 服务控制台形态 + Named Pipe + JSON-RPC 骨架（最小可用链路）
3) Mock 指标与 `snapshot` 打通 UI 调试
4) 日志/热重载/测试基线
5) 采集器与存储逐步补全

## 3. 风险与应对
- Pipe ACL/句柄泄漏 → 加强测试与诊断日志
- 序列化 snake_case 一致性 → 契约测试与统一配置
- 硬件差异/权限 → 模块可用性探测 `IsAvailable`

## 4. 关键技术验证
- StreamJsonRpc HeaderDelimited + SystemTextJsonFormatter 可行性
- Rust 命名管道客户端在 Tauri2 的可用性

## 5. 里程碑验收标准
- 按 `doc/task.md` 对应章节执行
