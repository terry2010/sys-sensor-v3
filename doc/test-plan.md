# 测试计划（草案）

## 1. 测试场景清单
- 协议：`hello/snapshot/set_config/start/stop` 正常/异常
- 事件：`metrics/state/alert/update_ready/ping` 序列与节流
- 断线重连：指数退避、重订阅
- 多客户端：并发订阅与限速
- 性能：采集延迟、内存、CPU、帧率（UI）
- 长稳：24h 无异常增长；日志轮转

### 1.1 set_config 模块过滤与速率整形
- 用例 A：`--module-intervals "cpu=300"`
  - 期望：`effective_intervals={cpu:300}`；`metrics` 仅含 `cpu` 字段
  - 校验：服务端日志包含 set_config 入参与归一化结果；client 打印的 params 与返回值一致
- 用例 B：`--base-interval 1000 --module-intervals "cpu=300;memory=1200"`
  - 期望：`effective_intervals={cpu:300,memory:1200}`；`metrics` 含 `cpu`,`memory`
  - 采样周期：推送间隔为 min(base,模块)；当进入 burst 覆盖时以 burst 间隔为准
- 用例 C：非法 interval（<100ms、非数字、未知模块）
  - 期望：返回 `invalid_params(-32001)` 或进行最小值钳制（>=100ms），并记录警告日志

### 1.2 命名与序列化契约
- 请求/响应字段统一 snake_case；服务端使用 `System.Text.Json` SnakeCaseLower 策略
- 单测：见 `SystemMonitor.Tests/UnitTest1.cs` 中 `SnakeCase_Serialization_ShouldUseSnakeCaseNames`

### 1.3 E2E：metrics 订阅与节流
- 用例 A：默认 `base_interval_ms=1000`，未订阅 burst
  - 期望：`metrics` 间隔约 1000ms；抖动 < 20%
- 用例 B：`burst_subscribe(interval_ms=300, ttl_ms=3000)`
  - 期望：在 TTL 内 `metrics` 间隔约 300ms；到期后恢复至 1000ms（允许一个周期内的过渡抖动）
- 校验：客户端统计收到事件时间戳序列，计算平均间隔/方差；服务端日志存在订阅/过期记录

### 1.4 E2E：query_history 跨模块聚合
- 准备：运行采集 ≥ 2 分钟，开启 `cpu/memory` 模块
- 调用：`query_history(from_ts, to_ts, modules=["cpu","memory"], step_ms=1000)`
- 期望：`items[].ts` 递增且覆盖区间；每条含请求的模块字段；不存在重复或跳点
- 边界：空区间返回 `ok:true, items:[]`

### 1.5 断线重连与多客户端
- 断线：Kill 服务进程 → 10s 内重启 → 客户端指数退避重连成功并恢复订阅
- 多客户端：2 个并发客户端订阅 → 都能收到事件；总吞吐维持在目标范围

### 1.6 错误路径
- 未携带 token 的 `hello` → `unauthorized(-32040)`
- 非法参数 → `invalid_params(-32001)` 且 `error.data` 指出字段
- 不支持的能力 → `not_supported(-32050)`

## 2. 性能基准
- 参考 `doc/task.md` NFR 表
  - 启动：服务冷启动 < 800ms（Release，本机）
  - 快照：`snapshot` 响应 < 50ms（95P）
  - 事件：在 300ms 间隔下抖动 < 20%，丢包率 0（单客户端）
  - 历史查询：`query_history` 1 分钟/双模块返回 < 200ms（95P）

## 3. 覆盖率目标
- 单元 > 80%；关键路径 > 90%

## 4. CI/CD 流程（预案）
- 触发：PR/主分支
- 阶段：构建→单测→契约测试（schema+snake_case）→打包→产物留存
 - 产出：`artifacts/test-results/*.trx`、失败时附带 `logs/service-*.log` 尾部
 - 阻断阈值：端到端关键用例失败即阻断；性能回归 > 20% 警告

## 5. 验收标准
- 里程碑 1/2 的验收条件满足；契约冻结后不破坏
 - API 变更必须先更新 `doc/api-reference.md` 与契约测试
