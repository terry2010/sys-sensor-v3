# JSON-RPC 接口规范（冻结）

> 传输：Named Pipes + HeaderDelimited + JSON-RPC 2.0；管道名：`\\.\pipe\sys_sensor_v3.rpc`；字段统一 `snake_case`。

> 冻结时间：2025-08-20 15:37（后续变更需评审并 bump `protocol_version` 或声明向后兼容策略）

## 1. 方法列表（M1 实装）
- `hello(params: { app_version: string, protocol_version: number, token: string, capabilities?: string[] })`
- `snapshot(params?: { modules?: string[] })`
- `set_config(params: { base_interval_ms?: number, module_intervals?: Record<string, number>, persist?: boolean })`
- `burst_subscribe(params: { modules?: string[], interval_ms: number, ttl_ms: number })`
- `start(params?: { modules?: string[] })`
- `stop(params: {})`
- `query_history(params: { from_ts: number, to_ts: number, modules?: string[], step_ms?: number })`

## 2. 事件（Service → UI）
- `metrics`: `{ ts: number, seq: number, cpu?: {...}, memory?: {...}, ... }`（按启用模块动态裁剪）
- `state`: `{ running: boolean, clients: number, effective_intervals: Record<string, number> }`
- `alert`: `{ level: "info"|"warn"|"error", metric: string, value: number, threshold?: number, rule_id?: string, message: string, ts: number }`
- `ping`: `{}`（可选心跳）
- `update_ready`: `{ component: string, version: string }`

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
// start response
{ "jsonrpc":"2.0","result": { "running": true }, "id":3 }
```

```json
// stop
{ "jsonrpc":"2.0","method":"stop","params": {}, "id":4 }
```
```json
// stop response
{ "jsonrpc":"2.0","result": { "running": false }, "id":4 }
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
    "cpu": { "usage_pct": 12.5 },
    "memory": { "used_mb": 2048 }
  },"id":6
}
```

```json
// query_history
{
  "jsonrpc":"2.0","method":"query_history","params":{
    "from_ts":1710000000,"to_ts":1710003600,
    "modules":["cpu","memory"],"step_ms":1000
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

> 注：`set_log_level/update_*` 非 M1 范围，后续里程碑再补充。

> 注：字段名以指标文档为准；所有键名均为 `snake_case`。

> 握手约束：
> - `token` 必须为非空字符串，否则返回 `unauthorized` 错误码。
> - 仅支持 `protocol_version = 1`，否则返回 `not_supported`（或明确的 `unsupported_version` 自定义码）。
> - `capabilities` 如包含未知能力，返回 `not_supported`。

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

## 5. 字段命名校验
- 服务端序列化策略：.NET `System.Text.Json` + `JsonNamingPolicy.SnakeCaseLower`
- 契约测试：对请求/响应进行 schema 与 snake_case 正则校验

## 6. 能力声明（capabilities）
- `metrics_stream`: 支持 metrics 推流
- `burst_mode`: 支持 `burst_subscribe`
- `history_query`: 支持 `query_history`

## 7. 版本与兼容
- `protocol_version` 初始为 1；破坏式变更需升版本并保持灰度兼容期
