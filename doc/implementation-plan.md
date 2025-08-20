# 实现计划（首版）

## 1. 依赖关系图
```mermaid
flowchart LR
  subgraph Service
    COL[Collectors/*] --> MCS[MetricsCollectionService]
    MCS --> AGG[Aggregator]
    AGG --> SQL[SqliteStore]
    RPC[RpcService] <---> NP[StreamJsonRpc/NamedPipe]
    RPC --> MCS
    RPC --> SQL
  end
  subgraph Frontend
    Hook[useJsonRpc] --> JS[jsonrpc.ts]
    JS --> RUST[Pipe Client (Rust)]
  end
  RUST <--> NP
```

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

## 5. 里程碑验收标准（M1/M2）
- M1（服务最小可用）：
  - JSON-RPC `hello/snapshot/set_config/start/stop` 通过契约与 E2E 测试
  - Named Pipe ACL 正确，日志与热重载可用
  - 指标 Mock 通路畅通，覆盖率达到目标
- M2（Tauri 最小界面）：
  - Tauri 单窗口初始化，Rust Pipe 客户端可连通
  - 调试窗口显示连接状态与 `snapshot` JSON
  - 断线重连/重订阅通过测试，性能达标
