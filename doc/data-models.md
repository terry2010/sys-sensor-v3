# 数据模型设计（v1 草案）

> 指标字段唯一来源：`doc/istat-menus-metrics.md`。以下仅定义结构与命名策略，具体字段在实现时逐项对照补全。

## 1. 指标结构（示意）
- __CPU__：`{ usage_percent, user, system, idle, load_avg_1m, load_avg_5m, load_avg_15m, processes, threads }`
- __Memory__：`{ total, used, available, pressure, swap_used, page_ins, page_outs }`
- __Disk__：`{ read_bytes_per_sec, write_bytes_per_sec, read_iops, write_iops, busy_percent }`
- __Network__：`{ rx_bytes_per_sec, tx_bytes_per_sec, rx_errors, tx_errors }`
- __GPU__：`{ usage_percent, vram_used, temperature }`
- __Sensors__：`{ temperatures: Record<string, number>, fans_rpm: Record<string, number> }`
- __Battery/Power__：`{ percent, charging, cycle_count, temperature, voltage_mv, current_ma }`

注：最终键名以指标文档为准，示意项仅作占位。

## 2. SQLite 表结构 DDL（草案）
```sql
-- 原始 1s
CREATE TABLE IF NOT EXISTS metrics_1s (
  ts INTEGER NOT NULL,
  module TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  PRIMARY KEY (ts, module)
) WITHOUT ROWID;

-- 聚合 10s / 1m（后续由 Aggregator 生成）
CREATE TABLE IF NOT EXISTS metrics_10s (
  ts INTEGER NOT NULL,
  module TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  PRIMARY KEY (ts, module)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS metrics_1m (
  ts INTEGER NOT NULL,
  module TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  PRIMARY KEY (ts, module)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS alerts (
  id TEXT PRIMARY KEY,
  ts INTEGER NOT NULL,
  level TEXT NOT NULL,
  metric TEXT NOT NULL,
  value TEXT NOT NULL,
  threshold TEXT,
  rule_id TEXT,
  message TEXT
);

CREATE TABLE IF NOT EXISTS config (
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  updated_at INTEGER NOT NULL
);
```

## 3. 内存映射文件（MMF）格式
- 名称：`Global/sys_sensor_v3_snapshot`
- 内容：`{ ts, seq, modules: { <module>: payload_json } }`
- 大小控制：环形缓冲 + 版本号，避免破坏式变更

## 4. 配置文件 JSON Schema（草案）
```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "title": "service_config",
  "type": "object",
  "properties": {
    "base_interval_ms": { "type": "integer", "minimum": 250 },
    "module_intervals": {
      "type": "object",
      "additionalProperties": { "type": "integer", "minimum": 250 }
    },
    "log_level": {
      "type": "string",
      "enum": ["verbose","debug","information","warning","error","fatal"]
    }
  },
  "additionalProperties": false
}
```

## 5. 命名与序列化策略
- 外部：snake_case；内部：语言惯例；通过统一序列化策略转换
