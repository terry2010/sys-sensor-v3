# 系统架构设计（v1 草案）

> 本文档根据 `doc/task.md` 要求，提供组件关系、数据流向、部署架构、窗口系统关系的初版定义，用于契约冻结前评审。

## 1. 组件关系图（ASCII）
```
[UI: Tauri(Vue3/TS)] --JsonRpc--> [Named Pipe 客户端(Rust插件)] --HeaderDelimited--> 
[Named Pipe 传输 \\.\pipe\sys_sensor_v3.rpc] --> [C# 服务(StreamJsonRpc 服务端)]
   |                                                                 |
   |<-- events(metrics/state/alert/update_ready/ping)  <-------------|
```

## 2. 数据流向
- __采集__: `Collectors/*` 周期采集 → `MetricsCollectionService` 聚合 → 内存快照/RingBuffer → 事件推送/SQLite 落盘
- __查询__: UI `snapshot/query_history` → RPC → 服务读取内存/SQLite → 响应
- __配置__: UI `set_config` → 服务生效（支持热重载）

## 3. 部署架构
- __进程__：
  - `SystemMonitor.Service`（控制台/Windows服务二选一形态）
  - `frontend`（Tauri 桌面应用）
- __文件路径__：参考 `doc/task.md` 的“数据管理方案/文件位置”
- __权限/安全__：Named Pipe 严格 ACL；Token 认证

## 4. 窗口系统关系（MVP范围）
- 单窗口（Debug/主窗口复用）
- 后续（两窗法/托盘/宠物）在对应里程碑前再冻结

## 5. 时序（握手/订阅示意）
1) UI 启动 → `hello`
2) 服务返回 `server_version/protocol_version`
3) UI 可 `start` / `burst_subscribe` / 轮询 `snapshot`

## 6. 非功能性要求绑定
- 启动、内存、CPU、帧率、长稳等参见 `doc/task.md` NFR

## 7. 待确认
- UI 运行在便携化场景下的数据路径策略
