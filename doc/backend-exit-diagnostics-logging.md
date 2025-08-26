# 后端服务退出诊断日志改进

## 概述

针对您提到的需要诊断C#后端退出时发生的问题，我已经为系统添加了详细的运行日志，特别是在连接断开、服务退出等关键时刻。这些日志将帮助您准确定位后端退出的原因。

## 新增的日志记录

### 1. Program.cs - 主程序生命周期日志

**启动阶段:**
- `[BOOT] Program start - PID: {进程ID}, 工作目录: {目录}`
- `[BOOT] 命令行参数: {参数列表}`
- `[BOOT] Host builder created`
- `[BOOT] Building host...`
- `[BOOT] Host built successfully, 进程ID: {进程ID}`
- `[BOOT] 开始运行 host...`

**运行模式识别:**
- 控制台模式: `[SystemMonitor.Service] Running in CONSOLE mode`
- 服务模式: `[SystemMonitor.Service] Running in WINDOWS SERVICE mode`

**退出阶段:**
- `[SHUTDOWN] ProcessExit 事件触发 - PID: {进程ID}`
- `[SHUTDOWN] 收到 Ctrl+C 信号 - PID: {进程ID}`
- `[SHUTDOWN] Host 运行结束`
- `[SHUTDOWN] Root command completed with return code: {返回码}`
- `[SHUTDOWN] Program ending - PID: {进程ID}, 退出时间: {时间戳}`

**异常处理:**
- `[FATAL] Service crashed - PID: {进程ID}`
- `[FATAL] 异常类型: {异常类型}`
- `[FATAL] 异常消息: {异常消息}`
- `[FATAL] 堆栈信息: {堆栈跟踪}`
- `[FATAL] 内部异常{深度}: {内部异常信息}`

### 2. Worker.cs - 后台工作服务日志

**生命周期日志:**
- `[worker] Worker 服务启动，进程ID: {进程ID}，线程ID: {线程ID}`
- `[worker] 收到停止信号，Worker 服务开始关闭流程`
- `[worker] Worker 已停止，总运行时间: {时间}，总循环次数: {次数}`

**运行状态日志:**
- 每1000次循环记录一次详细状态信息
- `[worker] Worker 运行状态: 循环次数={次数}, 运行时间={时间}, 当前时间={时间戳}`

**异常处理:**
- `[worker] Worker 执行异常，异常类型: {类型}，消息: {消息}`

### 3. RpcHostedService.cs - RPC服务核心日志

**服务启动:**
- `[startup] RPC HostedService 启动，进程ID: {进程ID}，线程ID: {线程ID}`
- `[startup] 收到停止信号，RPC HostedService 开始关闭流程`
- `[startup] 进入主连接监听循环`
- `[startup] 初始化 HistoryStore 开始/结束`

**连接管理:**
- `[startup] 创建命名管道实例，当前活跃连接数: {连接数}`
- `[startup] 接受到一个客户端连接，客户端进程ID: {客户端信息}`
- `[客户端] 客户端已连接，用户: {用户信息}`
- `[客户端] 客户端会话建立: conn={连接ID}，开始时间: {时间戳}`

**连接断开详情:**
- `[客户端] 客户端断开：{原因} conn={连接ID}，连接时长: {时长}，客户端: {客户端信息}`
- `[客户端] 断开异常详情: {异常类型} - {异常消息}`
- `[客户端] 处理断开事件时发生异常`

**Metrics推送循环:**
- `[metrics] 推送循环被取消: {消息}，会话结束 conn={连接ID}`
- `[metrics] 推送异常 conn={连接ID}，异常类型: {类型}，连续错误数: {次数}`
- `[metrics] 连续错误 {次数} 次，延迟 {毫秒}ms 后重试`

**资源清理:**
- `[rpc] 开始等待 RPC 完成信号 conn={连接ID}`
- `[rpc] RPC 完成信号已收到，开始清理任务 conn={连接ID}`
- `[cleanup] 等待 metrics 任务结束 conn={连接ID}`
- `[cleanup] metrics 任务结束时发生异常 conn={连接ID}`
- `[cleanup] 客户端会话已完全清理 conn={连接ID}`

**服务关闭:**
- `[shutdown] 开始清理所有活跃连接，当前连接数: {连接数}`
- `[shutdown] 等待会话结束超时，强制继续关闭流程`
- `[shutdown] 所有会话已正常结束`
- `[shutdown] RPC HostedService 已完全关闭`

**异常处理增强:**
- `[connection] RPC 会话 I/O 异常，异常类型: {类型}，错误代码: {代码}`
- `[connection] RPC 会话异常，异常类型: {类型}，消息: {消息}，堆栈信息: {堆栈}`
- `[critical] RPC HostedService 发生未处理的关键异常`

## 日志位置

日志文件位置: `{项目根目录}/logs/service-{日期}.log`

日志格式:
```
{时间戳} [{级别}] {机器名} {进程ID}/{线程ID} {组件名} {消息内容}
{异常信息}
```

## 使用方法

1. **实时监控日志:**
   ```powershell
   Get-Content -Path "logs\service-$(Get-Date -Format 'yyyy-MM-dd').log" -Wait
   ```

2. **查找特定问题:**
   ```powershell
   # 查找所有启动相关日志
   Select-String -Path "logs\*.log" -Pattern "\[BOOT\]|\[startup\]"
   
   # 查找所有退出相关日志
   Select-String -Path "logs\*.log" -Pattern "\[SHUTDOWN\]|\[shutdown\]"
   
   # 查找所有异常日志
   Select-String -Path "logs\*.log" -Pattern "\[FATAL\]|\[CRITICAL\]|异常"
   
   # 查找连接断开日志
   Select-String -Path "logs\*.log" -Pattern "断开|disconnected"
   ```

3. **诊断退出问题:**
   - 查看最后的 `[SHUTDOWN]` 日志确认正常退出流程
   - 查找 `[FATAL]` 日志确认崩溃原因
   - 检查连接相关日志确认是否因连接问题导致退出
   - 观察进程ID和线程ID追踪特定会话

## 环境变量配置

可以通过环境变量调整日志行为:

- `METRICS_LOG_EVERY`: 设置metrics推送计数日志频率
- `MAX_CONNECTION_AGE_MS`: 设置最大连接存活时间（默认1小时）
- `HEALTH_CHECK_INTERVAL_MS`: 设置健康检查间隔（默认30秒）

## 注意事项

1. 所有新增日志都不会影响现有功能，仅用于诊断
2. 日志级别设置为Information，确保关键信息被记录
3. 异常信息包含完整的堆栈跟踪，便于定位问题
4. 连接相关日志包含连接ID，便于追踪特定会话
5. 进程和线程ID有助于多实例环境下的问题定位

这些详细的日志将帮助您准确诊断后端退出的根本原因。