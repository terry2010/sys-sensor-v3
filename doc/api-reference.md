# JSON-RPC 接口规范（冻结）

> 传输：Named Pipes + HeaderDelimited + JSON-RPC 2.0；管道名：`\\.\pipe\sys_sensor_v3.rpc`；字段统一 `snake_case`。

> 冻结时间：2025-08-20 15:37（后续变更需评审并 bump `protocol_version` 或声明向后兼容策略）

## 1. 方法列表（M1 实装）
- `hello(params: { app_version: string, protocol_version: number, token: string, capabilities?: string[] })`
- `snapshot(params?: { modules?: string[] })`
- `set_config(params: { base_interval_ms?: number, module_intervals?: Record<string, number>, persist?: boolean })`
- `burst_subscribe(params: { modules?: string[], interval_ms: number, ttl_ms: number })`
- `start(params?: { modules?: string[] })`
- `stop()`
- `query_history(params: { from_ts: number, to_ts: number, modules?: string[], step_ms?: number })`
- `subscribe_metrics(params: { enable: boolean })`

## 2. 事件（Service → UI）
- `metrics`: `{ ts: number, seq: number, cpu?: {...}, memory?: {...}, ... }`（按启用模块动态裁剪；仅在“事件桥”连接且推流开关启用时发送）
- `state`（已实现，最小版）: `{ ts: number, phase: "start"|"stop"|"burst", reason?: string, extra?: any }`
  - 触发点：`start`、`stop`、`burst_subscribe` 成功后各发送一次；`ts` 为 UTC 毫秒时间戳。
- `alert`（预留，M1 未实现）: `{ level: "info"|"warn"|"error", metric: string, value: number, threshold?: number, rule_id?: string, message: string, ts: number }`
- `ping`（预留，M1 未实现）: `{}`（可选心跳）
- `update_ready`（预留，M1 未实现）: `{ component: string, version: string }`

## 3. 请求/响应示例
```json
// hello
{
  "jsonrpc":"2.0","method":"hello","params":{
    "app_version":"1.0.0","protocol_version":1,
    "token":"<token>","capabilities":["metrics_stream","burst_mode","history_query"]
  },"id":1
}
```
```json
// hello response
{
  "jsonrpc":"2.0","result":{
    "server_version":"1.0.0","protocol_version":1,
    "capabilities":["metrics_stream","burst_mode","history_query"],
    "session_id":"uuid-v4"
  },"id":1
}
```

```json
// set_config
{
  "jsonrpc":"2.0","method":"set_config","params":{
    "base_interval_ms":1000,
    "module_intervals": { "cpu": 300, "memory": 1200 },
    "persist": true
  },"id":2
}
```
```json
// set_config response
{ "jsonrpc":"2.0","result": { "ok": true, "base_interval_ms": 1000, "effective_intervals": { "cpu": 300 } }, "id":2 }
```

```json
// start (可选指定模块)
{ "jsonrpc":"2.0","method":"start","params": { "modules": ["cpu","memory"] }, "id":3 }
```
```json
// start response（与实现对齐）
{ "jsonrpc":"2.0","result": { "ok": true, "started_modules": ["cpu","mem"] }, "id":3 }
```

```json
// stop（无参）
{ "jsonrpc":"2.0","method":"stop","id":4 }
```
```json
// stop response（与实现对齐）
{ "jsonrpc":"2.0","result": { "ok": true }, "id":4 }
```

```json
// burst_subscribe
{
  "jsonrpc":"2.0","method":"burst_subscribe","params":{
    "modules":["cpu","net"],"interval_ms":1000,"ttl_ms":10000
  },"id":5
}
```
```json
// burst_subscribe response
{ "jsonrpc":"2.0","result": { "ok": true, "expires_at": 1710001000 }, "id":5 }
```

```json
// snapshot（未指定 modules 则返回默认指标）
{ "jsonrpc":"2.0","method":"snapshot","params": {}, "id":6 }
```
```json
// snapshot response（示例）
{
  "jsonrpc":"2.0","result": {
    "ts": 1710000000,
    "cpu": { "usage_percent": 12.5 },
    "memory": { "total": 16000, "used": 2048 }
  },"id":6
}
```

```json
// query_history
{
  "jsonrpc":"2.0","method":"query_history","params":{
    "from_ts":1710000000,
    "to_ts":1710003600,
    "modules":["cpu","memory"],
    "step_ms":1000
  },"id":7
}
```
```json
// query_history response（示例）
{
  "jsonrpc":"2.0","result": {
    "ok": true,
    "items": [
      { "ts":1710000000, "cpu": { "usage_percent": 10.1 }, "memory": { "total": 16000, "used": 7650 } }
    ]
  },"id":7
}
```

```json
// subscribe_metrics（可控全局推流开关；桥接连接默认已开启）
{ "jsonrpc":"2.0","method":"subscribe_metrics","params": { "enable": true }, "id":8 }
```

### query_history 语义说明

- 参数
  - `from_ts`/`to_ts`：毫秒级 UTC 时间戳，闭区间 `[from_ts, to_ts]`。
    - 约定：`to_ts = 0` 表示“到当前时刻”（服务端在查询时替换为当前 UTC 毫秒）。
  - `modules`：要返回的模块字段子集；缺省表示按当前启用模块动态裁剪。
  - `step_ms`：可选，>0 时按该步长聚合为时间桶，取每桶“最后一条记录”。

- 返回
  - `items`：按时间升序数组。元素结构：
    - `ts`: number
    - `cpu?`: `{ usage_percent: number }`
    - `memory?`: `{ total: number, used: number }`（单位 MB）

- 边界与回退
  - 若 SQLite 中窗口内无记录，服务端会回退到内存缓存或即时快照，通常至少返回 1 条，确保 `items.length >= 1`（视实现策略）。
  - 当 `modules` 仅含部分模块时，其它模块字段省略为 `null`/缺失。
  - `step_ms` 生效时，每个桶仅保留该桶内最后一条；桶无数据则不补零。窗口很短或数据极其稀疏时，可能退化为仅 1 个桶。

- 限制
  - 历史已接入 SQLite 持久化（当前 M1 为单表 `metrics`，含 `ts/cpu/mem_total/mem_used`），配合保留策略定期清理；重启后历史可用。后续里程碑可能扩展为多粒度表。

> 注：`set_log_level/update_*` 非 M1 范围，后续里程碑再补充。

> 注：字段名以指标文档为准；所有键名均为 `snake_case`。

> 握手约束：
> - `token` 必须为非空字符串，否则返回 `unauthorized` 错误。
> - 仅支持 `protocol_version = 1`，否则返回 `not_supported`。
> - `capabilities` 如包含未知能力，返回 `not_supported`。
> - 当 `capabilities` 含 `metrics_stream` 时，该连接被视为“事件桥”，服务端默认开启推流，并自动 `start({ modules: ["cpu","mem"] })`。
> - `subscribe_metrics` 可显式开关推流（全局）。

> 说明：
> - `set_config.persist` 在 M1 中暂不生效（忽略）。
> - `burst_subscribe.modules` 在 M1 中暂不使用（按全局速率控制）。

## 4. 错误响应与错误码
统一采用 JSON-RPC error 对象，`message` 为机器可读短语，`data` 可包含细节：
```json
{
  "jsonrpc":"2.0",
  "error": { "code": -32001, "message": "invalid_params", "data": { "field": "interval_ms" } },
  "id": 1
}
```

推荐错误码（私有区间 -32000~-32099）：
- unauthorized: -32040（示例：`hello` 缺少/无效 token）
- invalid_params: -32001
- not_supported / unsupported_version: -32050
- rate_limited: -32060
- internal: -32099

> 说明：M1 阶段实现侧可能返回框架默认错误码；上述取值为推荐值，后续里程碑将对齐具体 code。

## 5. 字段命名校验
- 服务端序列化策略：.NET `System.Text.Json` + `JsonNamingPolicy.SnakeCaseLower`
- 契约测试：对请求/响应进行 schema 与 snake_case 正则校验

## 6. 能力声明（capabilities）
- `metrics_stream`: 支持 metrics 推流
- `burst_mode`: 支持 `burst_subscribe`
- `history_query`: 支持 `query_history`

## 7. 版本与兼容
- `protocol_version` 初始为 1；破坏式变更需升版本并保持灰度兼容期
