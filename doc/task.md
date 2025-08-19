

# Windows系统监控工具开发规范 v3.0

## 项目概述

**目标**：在Windows上开发对标iStat Menus的系统监控工具，额外提供桌面宠物交互功能。

**核心要求**：
- **指标权威文档**：`C:\code\sys-sensor-v3\doc\istat-menus-metrics.md`（所有指标字段与含义的唯一来源，以下简称"指标文档"）
- **架构**：C# 后台服务（采集/规则/存储/更新）+ Tauri 2.x + Vue3 + TypeScript前端（展示/交互/动画）
- **通信**：Windows Named Pipes + JSON-RPC 2.0（HeaderDelimited分帧），仅本机通信，不开网络端口
- **开发环境**：Windows 10 、Visual Studio 2022、Node.js 18+、PowerShell（多命令同行用`;`分隔）
- **运行环境**：win10，win11

## 硬性约束与通用约定

1. **命名规范（强制）**
   - 外部数据（IPC消息/持久化存储/对外文档）统一使用`snake_case`
   - 内部代码遵循语言惯例（C# PascalCase/camelCase；TypeScript camelCase）
   - 通过序列化策略自动转换：
     - .NET 8: `System.Text.Json` with `JsonNamingPolicy.SnakeCaseLower`
     - StreamJsonRpc: `SystemTextJsonFormatter` + 定制`JsonSerializerOptions`

2. **指标字段（强制）**：严禁自创/修改字段名，必须与"指标文档"完全一致；示例仅作形态参考，不作字段命名来源

3. **代码规范（强制）**
   - 所有代码注释必须使用中文，包含参数说明、返回值、错误处理和使用示例
   - 单文件建议不超过300行
   - 不能吞异常，必须记录日志
   - 资源必须正确Dispose
   - 安全原则：最小权限运行、严格ACL控制、token认证、签名校验

## 系统架构

```
┌─────────────────────────────────────────────────────────────────┐
│                        用户界面层                                 │
├──────────┬──────────┬──────────┬──────────┬────────────────────┤
│ 系统托盘 │ 悬浮窗口 │ 贴边窗口 │ 设置窗口 │   桌面宠物窗口     │
│(动态图标)│(数据展示)│(两窗法)  │(配置管理)│  (状态机动画)      │
├──────────┴──────────┴──────────┴──────────┴────────────────────┤
│              Tauri前端应用 (Vue3 + TypeScript)                   │
│              - JsonRpc客户端                                     │
│              - 数据Store（最新快照）                             │
│              - 渲染侧合帧（~250ms）                              │
├─────────────────────────────────────────────────────────────────┤
│         Named Pipes (\\.\pipe\sys_sensor_v3.rpc)                │
│         JSON-RPC 2.0 + HeaderDelimited + Token认证               │
├─────────────────────────────────────────────────────────────────┤
│                  C#后台服务 (.NET 8)                             │
├──────────┬──────────┬──────────┬──────────┬────────────────────┤
│数据采集器│ 规则引擎 │ 数据存储 │ RPC服务  │   更新管理器       │
│(1-60s)   │(阈值告警)│(SQLite)  │(推流)    │  (静默下载)        │
├──────────┴──────────┴──────────┴──────────┴────────────────────┤
│              Windows系统API / 硬件接口                            │
└─────────────────────────────────────────────────────────────────┘
```

## 技术栈详细说明

### 后端（C# ）
- **核心框架**：.NET Worker Service（支持Windows服务+控制台双形态）
- **通信**：StreamJsonRpc + NamedPipeServerStream（HeaderDelimitedMessageHandler + SystemTextJsonFormatter）
- **数据采集**：LibreHardwareMonitor、PerformanceCounter/PDH、WMI/CIM、ETW、NVML/ADL
- **数据存储**：Microsoft.Data.Sqlite（WAL模式，直接SQL优先）、Entity Framework Core（可选）
- **日志监控**：Serilog（RollingFile + EventLog）、OpenTelemetry Metrics（可选，仅127.0.0.1）

### 前端（Tauri 2.x + Vue3）
- **核心框架**：Tauri 2.x、Vue 3 + TypeScript、Vite
- **Tauri插件**：@tauri-apps/plugin-tray、plugin-updater、plugin-positioner、plugin-global-shortcut
- **自研Rust插件**：pipe（Named Pipe客户端，MVP 必选）；input/window_anim（后置，可选）
- **UI与动画**：Element Plus、ECharts/Chart.js、lottie-web/PixiJS、CSS transform（复杂动画后置，可选）

说明：MVP 仅需单窗口 + 调试视图 + JSON-RPC 客户端。多窗口、贴边、动画、宠物等放在协议冻结后实施。

## 通信协议规范（JSON-RPC 2.0）

### 传输层配置
```
管道名称: \\.\pipe\sys_sensor_v3.rpc
编码: UTF-8 (无BOM)
分帧格式: HeaderDelimited
  Content-Length: <len>\r\n
  \r\n
  {json_payload}

ACL配置:
- SYSTEM: FullControl
- Administrators: FullControl  
- 当前交互用户: ReadWrite
- 其他: 拒绝所有

心跳与重连:
- 30s无事件时服务端可发送ping
- 客户端指数退避重连（初始500ms，最大10s）
- 重连成功需重新hello/订阅
```

### 握手协议
```json
// Request (UI → Service)
{
  "jsonrpc": "2.0",
  "method": "hello",
  "params": {
    "app_version": "1.0.0",
    "protocol_version": 1,
    "token": "<token_from_%ProgramData%\\sys-sensor-v3\\token>",
    "capabilities": ["metrics_stream", "burst_mode", "history_query"]
  },
  "id": 1
}

// Response
{
  "jsonrpc": "2.0",
  "result": {
    "server_version": "1.0.0",
    "protocol_version": 1,
    "capabilities": ["metrics_stream", "burst_mode", "history_query"],
    "session_id": "uuid-v4"
  },
  "id": 1
}
```

### 核心方法定义

| 方法 | 参数 | 返回值 | 说明 |
|------|------|--------|------|
| `hello` | `{app_version, protocol_version, token, capabilities?}` | `{server_version, protocol_version, capabilities}` | 握手认证 |
| `set_config` | `{base_interval_ms?, module_intervals?, persist?}` | `{ok, effective_intervals}` | 配置采集参数 |
| `start` | `{modules?}` | `{ok, started_modules}` | 启动采集 |
| `stop` | `{}` | `{ok}` | 停止采集 |
| `burst_subscribe` | `{modules, interval_ms, ttl_ms}` | `{ok, expires_at}` | 临时高频订阅 |
| `snapshot` | `{modules?}` | `{ts, ...各模块字段}` | 获取即时快照 |
| `query_history` | `{start_ts, end_ts, modules?, granularity?}` | `{data:[]}` | 查询历史数据 |
| `set_log_level` | `{level}` | `{ok, previous_level}` | 调整日志级别 |
| `update_check` | `{}` | `{available, version?, notes?}` | 检查更新 |
| `update_apply` | `{version?}` | `{ok}` | 应用更新 |

### 事件通知（Service → UI）
- `metrics`: `{ts, seq, ...各模块字段}`（推流数据，字段严格按指标文档）
- `state`: `{running, clients, effective_intervals}`（服务状态）
- `alert`: `{level, metric, value, threshold?, rule_id?, message, ts}`（告警事件）
- `update_ready`: `{component, version}`（更新就绪）
- `ping`: `{}`（可选心跳）

### 错误码规范

| 错误码 | 含义 | 处理建议 |
|--------|------|----------|
| -32001 | invalid_params | 检查参数格式 |
| -32002 | unauthorized | 重新认证token |
| -32003 | unsupported_version | 升级客户端 |
| -32004 | rate_limited | 降低请求频率 |
| -32005 | module_unavailable | 检查硬件支持 |
| -32006 | internal_error | 查看服务日志 |

## 功能模块详细设计

### 1. 智能功耗管理
- **Active模式**：有连接，正常采集(1s)
- **Standby模式**：无连接<5分钟，降频(5s)  
- **LowPower模式**：无连接>5分钟，最小采集(30s)
- **Suspended模式**：无连接>30分钟，暂停采集

### 2. 贴边窗口两窗法
- **主窗口**：数据展示，透明/置顶/跳过任务栏
- **哨兵窗口**：2px宽把手，可交互
- **交互逻辑**：悬停招手→点击滑出→GPU加速动画

### 3. 桌面宠物状态机
- **状态**：IDLE、HIDDEN、TEASING、CHASING、ALERT、SHOWING
- **触发**：基于系统指标、鼠标交互、时间条件
- **动画**：Lottie/Sprite/Canvas，60fps（电池模式30fps）
- **脚本引擎**：JavaScript沙箱，限制危险操作

### 4. 数据采集器接口
```csharp
public interface IMetricsCollector
{
    string ModuleName { get; }
    TimeSpan RecommendedInterval { get; }
    bool IsAvailable();
    Task<Dictionary<string, object>> CollectAsync(CancellationToken cancellationToken);
}
```

## 数据管理方案

### 存储架构
```
内存层（实时）
├── 最新快照（CurrentSnapshot）
├── 环形缓冲（RingBuffer<60s>）
└── 内存映射文件（MMF: Global\sys_sensor_v3_snapshot）

持久层（SQLite）
├── metrics_1s   - 24小时原始数据
├── metrics_10s  - 7天聚合数据
├── metrics_1m   - 30天聚合数据
├── alerts       - 告警历史
└── config       - 配置版本

文件位置
├── %ProgramData%\sys-sensor-v3\
│   ├── config.json        (服务配置，FileSystemWatcher热重载)
│   ├── token             (认证token, ACL 600)
│   ├── data.db           (SQLite数据库，WAL模式)
│   └── logs\             (日志目录)
└── %AppData%\sys-sensor-v3\
    ├── settings.json     (UI设置)
    └── pet_scripts\      (宠物脚本)
```

## 安全机制

1. **Named Pipe安全**：严格ACL控制，仅SYSTEM/Administrators/当前用户
2. **Token认证**：%ProgramData%\sys-sensor-v3\token，首次握手强制校验
3. **服务控制**：安装时授予普通用户Start/Stop权限，避免UAC
4. **更新安全**：签名验证、原子替换、失败回滚、仅updater.exe提权

## 性能要求（NFR）

| 指标 | 目标值 | 测量方法 |
|------|--------|----------|
| UI首次可见 | <800ms | 冷启动到窗口显示 |
| 托盘响应 | <100ms | 点击到菜单显示 |
| 服务启动 | <3s | 服务启动到就绪 |
| 内存-服务 | 30-80MB (AOT: 10-25MB) | Task Manager |
| 内存-UI | <120MB常驻 | Task Manager |
| CPU-空闲 | ≈0% | Performance Monitor |
| 动画帧率 | 60fps (电池30fps) | 渲染计数器 |
| 24h稳定性 | 内存增长<10MB | 长时间监控 |
| 更新停机 | <5s | 停服到启服时间 |

## 项目结构
```
C:\code\sys-sensor-v3\
├── doc\
│   ├── istat-menus-metrics.md      # 指标权威文档
│   ├── api-reference.md            # JSON-RPC接口文档
│   └── architecture.md             # 架构设计文档
├── src\
│   ├── SystemMonitor.Service\      # C#后台服务
│   │   ├── Program.cs              # 入口（双形态支持）
│   │   ├── Services\
│   │   │   ├── MetricsCollectionService.cs  # 采集调度
│   │   │   ├── RpcService.cs               # JSON-RPC服务
│   │   │   ├── DataStorageService.cs       # 数据存储
│   │   │   ├── PowerManagementService.cs   # 功耗管理
│   │   │   └── UpdateService.cs            # 更新服务
│   │   ├── Collectors\              # 各指标采集器
│   │   │   ├── Base\
│   │   │   │   └── IMetricsCollector.cs
│   │   │   ├── CpuCollector.cs
│   │   │   ├── MemoryCollector.cs
│   │   │   ├── DiskCollector.cs
│   │   │   ├── NetworkCollector.cs
│   │   │   ├── GpuCollector.cs
│   │   │   └── SensorCollector.cs
│   │   ├── Models\
│   │   │   ├── Metrics\            # 指标数据模型
│   │   │   ├── JsonRpc\            # RPC消息模型
│   │   │   └── Configuration\      # 配置模型
│   │   ├── Rules\
│   │   │   └── AlertRulesEngine.cs # 告警规则引擎
│   │   ├── Persistence\
│   │   │   ├── SqliteStore.cs     # SQLite存储
│   │   │   └── Aggregator.cs      # 数据聚合
│   │   └── Utils\
│   │       ├── Security\           # 安全相关
│   │       └── Serialization\      # 序列化配置
│   ├── SystemMonitor.Tests\        # 单元测试
│   │   ├── UnitTests\
│   │   ├── IntegrationTests\
│   │   └── ContractTests\          # 契约测试
│   └── frontend\                    # Tauri前端
│       ├── src-tauri\
│       │   ├── src\
│       │   │   ├── main.rs         # Tauri主入口
│       │   │   ├── pipe_client.rs  # Named Pipe客户端
│       │   │   └── plugins\        # 自研插件
│       │   │       ├── pipe\       # Named Pipe插件
│       │   │       ├── window_anim\# 窗口动画插件
│       │   │       └── input\      # 输入监听插件
│       │   └── Cargo.toml
│       ├── src\
│       │   ├── api\                # API层
│       │   │   └── jsonrpc.ts     # JSON-RPC客户端
│       │   ├── components\         # 通用组件
│       │   │   ├── TrayIcon.vue
│       │   │   ├── FloatingWindow.vue
│       │   │   ├── EdgeDockWindow.vue
│       │   │   └── DesktopPet.vue
│       │   ├── composables\        # 组合式函数
│       │   │   ├── useJsonRpc.ts
│       │   │   └── useSystemMetrics.ts
│       │   ├── stores\             # 状态管理
│       │   │   ├── metrics.ts
│       │   │   └── settings.ts
│       │   ├── views\              # 页面视图
│       │   │   ├── DebugView.vue
│       │   │   ├── SettingsView.vue
│       │   │   └── DataRawView.vue
│       │   ├── windows\            # 多窗口入口
│       │   │   ├── main\
│       │   │   ├── floating\
│       │   │   ├── docking\
│       │   │   └── pet\
│       │   └── assets\
│       │       └── animations\     # Lottie动画文件
│       └── tests\
├── scripts\                         # 构建/部署脚本
│   ├── build.ps1
│   ├── install-service.ps1
│   └── package.ps1
├── installer\                       # 安装程序
│   └── setup.iss                  # Inno Setup脚本
└── updater\                        # 更新程序
    └── src\
        └── updater.cs              # 高权限更新执行器
```

## 开发计划（里程碑）—

### 里程碑1：后端最小可用骨架（第1周）
- [ ] Named Pipe 服务端 + ACL 配置（仅本机）
- [ ] JSON-RPC 协议骨架（hello/snapshot/set_config/start/stop）
- [ ] 双形态运行（Windows 服务 + 控制台）
- [ ] Serilog 日志、基础配置热重载
- [ ] Mock 指标生成器（用于打通 UI）
- [ ] 基础单元测试（协议/服务生命周期）

验收：控制台模式下可接受 hello/snapshot，日志正常，Mock 数据可返回。

### 里程碑2：Tauri 最小界面（第2周）
- [ ] Tauri 2.x 项目初始化（单窗口）
- [ ] Rust pipe 客户端最小实现（或临时 Node IPC 方案）
- [ ] JSON-RPC 客户端封装 + 自动重连
- [ ] 最小“调试窗口”：仅显示连接状态与 snapshot 原始 JSON
- [ ] 设置持久化最小实现（interval 等）

验收：启动 UI 可连接服务并每秒显示 snapshot 原始数据。

### 里程碑3：C# 指标模块与数据面（第3-5周）
- [ ] 采集器全集：CPU/Memory/Disk/Network/GPU/Sensor（字段以“指标文档”为唯一来源）
- [ ] 采集调度与功耗管理（Active/Standby/LowPower/Suspended）
- [ ] SQLite 存储与聚合（1s 原始、10s/1m 聚合）
- [ ] 历史查询 `query_history`、事件 `metrics/state`
- [ ] 内存快照与统一读取接口
- [ ] 错误码/异常与日志打点完善
- [ ] 针对采集器与协议的单元/集成测试

验收：无 UI 干预下服务可稳定运行 24h，采集与历史查询准确，内存增长 <10MB。

### 里程碑4：协议冻结与契约测试（第6周）
- [ ] JSON-RPC 方法/事件/错误码定版（对齐“指标文档”字段）
- [ ] 契约测试（schema 校验、snake_case 验证）
- [ ] 回归测试用例基线

验收：协议契约冻结，之后 UI 仅对契约消费，无需后端再破坏式变更。

### 里程碑5：界面体系建设（第7-8周）
- [ ] 系统托盘（动态图标可后置）
- [ ] 悬浮窗口与贴边窗口（两窗法最小实现）
- [ ] 设置窗口（指标勾选、刷新率、主题等）
- [ ] 数据展示基础组件（实时卡片 + 历史图表）
- [ ] 多显示器与窗口位置记忆

验收：用户可通过 UI 配置与查看核心指标与历史图。

### 里程碑6：高级功能（第9-10周）
- [ ] 阈值规则与告警事件、通知
- [ ] 桌面宠物（状态机/动画/脚本“可选”）
- [ ] 更新系统（UI 与服务），签名校验
- [ ] 性能优化（AOT/内存/帧率）

### 里程碑7：测试与交付（第11-12周）
- [ ] 覆盖率 >80%、长稳与性能基准通过
- [ ] 安装器、诊断包、用户文档/开发者文档
- [ ] 最终验收

## 测试策略

### 1. 单元测试
- 各采集器独立测试
- 协议方法测试
- 状态机测试
- 工具类测试

### 2. 契约测试
- JSON-RPC请求/响应格式
- 字段名snake_case验证
- 配置文件JSON Schema验证

### 3. 集成测试
- 完整数据流测试
- 多客户端并发测试
- 断线重连测试
- 更新流程测试

### 4. 性能测试
- 采集延迟<50ms
- 内存占用基准
- CPU使用率测试
- 动画帧率测试

### 5. 长稳测试
- 24小时连续运行
- 内存增长监控
- 句柄泄漏检查
- 日志轮转验证

## 代码质量要求

### 1. 注释规范示例
```csharp
/// <summary>
/// 采集CPU指标数据
/// </summary>
/// <param name="cancellationToken">用于取消操作的令牌</param>
/// <returns>包含CPU各项指标的字典，字段名遵循snake_case</returns>
/// <exception cref="CollectorException">采集失败时抛出</exception>
/// <example>
/// var cpuData = await collector.CollectAsync(token);
/// var usage = cpuData["usage_percent"];
/// Console.WriteLine($"CPU使用率: {usage}%");
/// </example>
public async Task<Dictionary<string, object>> CollectAsync(
    CancellationToken cancellationToken)
{
    // 实现代码
}
```

### 2. 错误处理原则
- 不吞异常，必须记录日志
- 提供有意义的错误信息
- 资源清理使用finally或using
- 区分可恢复和不可恢复错误

### 3. 提交规范
- feat: 新功能
- fix: 修复bug
- docs: 文档更新
- style: 代码格式
- refactor: 重构
- test: 测试相关
- chore: 构建/工具

## 交付物清单

1. **源代码**
   - 完整的C#后端服务代码（含中文注释）
   - Tauri前端应用代码
   - 自研Rust插件代码
   - 单元测试和集成测试

2. **文档**
   - JSON-RPC接口文档（含示例）
   - 指标字段对照表
   - 部署指南
   - 用户手册
   - 开发者文档

3. **构建产物**
   - Windows服务安装包
   - Tauri应用安装包
   - 更新包（含签名）
   - 便携版（可选）

4. **脚本与工具**
   - PowerShell构建脚本
   - 服务安装/卸载脚本
   - 诊断信息收集工具
   - 性能测试脚本

## 实现前必须输出（强制要求）

**在生成任何代码前，AI必须先输出以下设计文档，用于校对与冻结契约：**

注意：MVP 阶段（里程碑1-4）必须先冻结 JSON-RPC 契约与数据模型；窗口系统/动画/宠物等文档在进入对应里程碑前再冻结，避免前置过早设计造成返工。

### 1. 系统架构图（ASCII/Mermaid）
- 组件关系图
- 数据流向图
- 部署架构图
- 窗口系统关系图

### 2. JSON-RPC接口规范（最终版）
- 完整方法列表（含参数类型）
- 每个方法的请求/响应示例（JSON格式）
- 所有事件通知格式
- 错误响应示例
- 字段命名验证（必须snake_case）

### 3. 数据模型设计
- 各模块指标结构（与"指标文档"逐一对照）
- SQLite表结构DDL
- 内存映射文件格式
- 配置文件JSON Schema

### 4. 窗口系统设计
- 窗口清单（名称、用途、属性）
- 窗口创建参数详情
- 两窗法实现细节
- 动画策略与触发条件
- 点击穿透切换逻辑

### 5. 安全设计文档
- 管道名称与ACL详细配置
- Token生成算法与存储位置
- 握手流程时序图
- 更新签名验证流程
- 权限最小化清单

### 6. 测试计划
- 测试场景清单（按优先级）
- 性能测试基准值
- 覆盖率目标（模块级）
- CI/CD流程图
- 验收标准

### 7. 实现计划
- 模块依赖关系图
- 开发顺序（含理由）
- 风险评估与应对
- 关键技术验证点
- 里程碑验收标准

### 8. 补充说明
- PowerShell命令示例（多命令用`;`）
- 调试模式说明（WebSocket临时端口等）
- 配置热重载机制
- 降级方案
- 已知限制

**注意事项**：
1. 如有任何信息缺失或歧义，必须先向用户提问澄清
2. 示例中的指标字段名仅作形态演示，实际实现必须以"指标文档"为准
3. 默认不开任何网络端口，如需临时调试端口必须明确说明用途和关闭方法
