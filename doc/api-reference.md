# JSON-RPC 接口规范（最终版草案）

> 传输：Named Pipes + HeaderDelimited + JSON-RPC 2.0；管道名：`\\.\pipe\sys_sensor_v3.rpc`；字段统一 `snake_case`。

## 1. 方法列表
- `hello(params: { app_version: string, protocol_version: number, token: string, capabilities?: string[] })`
- `set_config(params: { base_interval_ms?: number, module_intervals?: Record<string, number>, persist?: boolean })`
- `start(params?: { modules?: string[] })`
- `stop(params: {})`
- `burst_subscribe(params: { modules: string[], interval_ms: number, ttl_ms: number })`
- `snapshot(params?: { modules?: string[] })`
- `query_history(params: { start_ts: number, end_ts: number, modules?: string[], granularity?: '1s'|'10s'|'1m' })`
- `set_log_level(params: { level: 'verbose'|'debug'|'information'|'warning'|'error'|'fatal' })`
- `update_check(params: {})`
- `update_apply(params: { version?: string })`

## 2. 事件（Service → UI）
- `metrics`: `{ ts: number, seq: number, ...模块字段 }`
- `state`: `{ running: boolean, clients: number, effective_intervals: Record<string, number> }`
- `alert`: `{ level: 'info'|'warn'|'error', metric: string, value: number|string, threshold?: number|string, rule_id?: string, message: string, ts: number }`
- `update_ready`: `{ component: string, version: string }`
- `ping`: `{}`

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
{ "jsonrpc":"2.0","result": { "ok": true }, "id":2 }
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
{ "jsonrpc":"2.0","result": { "subscription_id": "uuid-v4" }, "id":5 }
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
    "start_ts":1710000000,"end_ts":1710003600,
    "modules":["cpu"],"granularity":"10s"
  },"id":7
}
```
```json
// query_history response（示例）
{
  "jsonrpc":"2.0","result": {
    "series": [{ "ts":1710000000, "cpu": { "usage_pct": 10.1 } }]
  },"id":7
}
```

```json
// set_log_level
{ "jsonrpc":"2.0","method":"set_log_level","params": { "level":"warning" }, "id":8 }
```
```json
// set_log_level response
{ "jsonrpc":"2.0","result": { "effective":"warning" }, "id":8 }
```

```json
// update_check
{ "jsonrpc":"2.0","method":"update_check","params": {}, "id":9 }
```
```json
// update_check response
{ "jsonrpc":"2.0","result": { "latest":"1.1.0", "has_update": true }, "id":9 }
```

```json
// update_apply
{ "jsonrpc":"2.0","method":"update_apply","params": { "version": "1.1.0" }, "id":10 }
```
```json
// update_apply response
{ "jsonrpc":"2.0","result": { "accepted": true }, "id":10 }
```

> 注：字段名以指标文档为准；所有键名均为 `snake_case`。

## 4. 错误响应
```json
{
  "jsonrpc":"2.0",
  "error": { "code": -32001, "message": "invalid_params", "data": { "field": "interval_ms" } },
  "id": 1
}
```

## 5. 字段命名校验
- 服务端序列化策略：.NET `System.Text.Json` + `JsonNamingPolicy.SnakeCaseLower`
- 契约测试：对请求/响应进行 schema 与 snake_case 正则校验

## 6. 版本与兼容
- `protocol_version` 初始为 1；破坏式变更需升版本并保持灰度兼容期
