# 数据模型设计（v1 实装）

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

## 2. SQLite 表结构 DDL（当前实现 + 规划）
```sql
-- 原始 1s
CREATE TABLE IF NOT EXISTS metrics_1s (
  ts INTEGER NOT NULL,
  module TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  PRIMARY KEY (ts, module)
) WITHOUT ROWID;

-- 聚合 10s / 1m（规划：由 Aggregator 生成）
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

### 2.1 索引与查询策略
- 典型查询：`query_history(from_ts, to_ts, modules[], step_ms)`
  - 范围扫描走 `PRIMARY KEY(ts, module)`；按 `module IN (...)` 过滤
  - 视图建议：`v_metrics(ts, module, payload_json)` 聚合 `metrics_1s/10s/1m`（便于根据 `step_ms` 选择最合适的明细/聚合源）
- 补充索引（当前建议，按需要开启）：
  - `CREATE INDEX IF NOT EXISTS idx_m1s_module_ts ON metrics_1s(module, ts);`
  - `CREATE INDEX IF NOT EXISTS idx_m10s_module_ts ON metrics_10s(module, ts);`
  - `CREATE INDEX IF NOT EXISTS idx_m1m_module_ts ON metrics_1m(module, ts);`

### 2.2 保留策略（Retention）
- `metrics_1s`：保留 2 天（已启用）
- `metrics_10s`：保留 14 天（规划）
- `metrics_1m`：保留 90 天（规划）
- 通过定时清理任务执行 `DELETE FROM <table> WHERE ts < cutoff`；定期 `VACUUM` 评估与窗口维护

## 3. 内存映射文件（MMF）格式（实现中）
- 名称：`Global/sys_sensor_v3_snapshot`
- 内容：`{ ts, seq, modules: { <module>: payload_json } }`
- 大小控制：环形缓冲 + 版本号，避免破坏式变更

### 3.0 字段定义（草案）
- `version`：数字，快照结构版本（破坏式变更时递增）
- `ts`：Unix 毫秒时间戳
- `seq`：自增序号（uint32，溢出回绕）
- `modules`：对象映射，键为模块名（如 `cpu/memory/...`），值为 JSON 字符串或已解析对象（实现可选）
- `checksum`：（可选）CRC32/xxHash，总体一致性 quick check

### 3.0.1 访问与并发
- 写侧采用单写者策略；读侧可多读者，建议快照复制以避免读写竞争
- 发生结构升级时以 `version` 判定向后兼容路径

### 3.1 快照到事件映射
- 采集器写入内存快照；推送 `metrics` 事件携带同一份 payload 的精简结构
- `burst_subscribe` 期间增加频度，不改变数据结构

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

### 5.0 命名校验流程
1) 以 `doc/istat-menus-metrics.md` 为对照表，生成字段清单
2) 在测试中对所有 JSON 输出执行正/负用例校验：
   - 正例：仅包含对照表中允许字段，全部 `snake_case`
   - 负例：驼峰/帕斯卡/拼写错误必须触发失败
3) 对 Schema（如 `query_history` 返回）进行 JSON Schema 校验

### 5.1 模块字段清单（占位）
- `cpu`：`{ usage_percent, user, system, idle, load_avg_1m, load_avg_5m, load_avg_15m, process_count, thread_count, per_core[], current_mhz?, max_mhz?, top_processes?[], context_switches_per_sec?, syscalls_per_sec?, interrupts_per_sec? }`
  - `top_processes`：数组，元素结构为 `{ name: string, pid: number, cpu_percent: number }`
    - 取值：按 CPU% 降序的前 N（当前 N=5），范围 0..100；不可得进程名时 `name="(unknown)"`
    - 采样：差分 `Process.TotalProcessorTime`，按逻辑核数归一；内部 800ms 节流缓存
  - `context_switches_per_sec?`：number | null（>=0）
  - `syscalls_per_sec?`：number | null（>=0）
  - `interrupts_per_sec?`：number | null（>=0）
    - 来源：Windows Performance Counters，优先类别 `System`（`Context Switches/sec`、`System Calls/sec`、`Interrupts/sec`）；回退 `Processor Information` 的 `_Total` 实例
    - 采样：读取速率型计数器，内部 200ms 节流缓存；读取异常/不可得时返回 `null`
- `memory`：`{ total, used, available, pressure }`
- `disk`：`{ read_bytes_per_sec, write_bytes_per_sec, read_iops, write_iops, busy_percent }`
- `network`：`{ rx_bytes_per_sec, tx_bytes_per_sec, rx_errors, tx_errors }`
- `gpu`：`{ usage_percent, vram_used, temperature }`

### 5.2 命名一致性约束
- 对外一律 `snake_case`；禁止驼峰/帕斯卡混用
- 单位约定：以 `*_percent`, `*_bytes_per_sec`, `*_mb` 等后缀清晰表达
- 破坏式字段变更需同步 `protocol_version` 升级与兼容处理

## 6. 聚合产物与查询映射
- 当前：直接从 `metrics_1s` 读取，并在服务端进行 `step_ms` 分桶聚合（取桶内最后一条）。
- 规划：Aggregator 周期性将 `metrics_1s` 聚合到 `metrics_10s/metrics_1m`，并按下述选择逻辑：
  - `step_ms <= 1000` → `metrics_1s`
  - `1000 < step_ms <= 10000` → `metrics_10s`
  - `step_ms > 10000` → `metrics_1m`
- 跨模块查询：对相同 `ts` 的多模块记录合并返回；缺失模块可省略/为 null。
