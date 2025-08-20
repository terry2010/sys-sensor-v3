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

## 2. 性能基准
- 参考 `doc/task.md` NFR 表

## 3. 覆盖率目标
- 单元 > 80%；关键路径 > 90%

## 4. CI/CD 流程（预案）
- 触发：PR/主分支
- 阶段：构建→单测→契约测试（schema+snake_case）→打包→产物留存

## 5. 验收标准
- 里程碑 1/2 的验收条件满足；契约冻结后不破坏
