# 使用说明（SystemMonitor V3）

本说明覆盖服务端启动/停止、客户端运行与日志定位、调试技巧（metrics 计数、乱码修复等）。

## 目录结构
- 服务端：`src/SystemMonitor.Service/`
- 客户端：`src/SystemMonitor.Client/`
- 日志目录：`logs/`
- 一键停止脚本：`scripts/kill-service.ps1`

## 构建
```powershell
# 在仓库根目录
dotnet build SysSensorV3.sln -c Debug -v minimal
```

## 启动服务（后台运行）
```powershell
# 可选：每推送 N 条 metrics 打一条累计日志（便于观察）
$env:METRICS_LOG_EVERY = 10

# 后台启动服务并将输出写入 logs/service.*.log
$p = Start-Process dotnet `
  -ArgumentList 'src\SystemMonitor.Service\bin\Debug\net8.0\SystemMonitor.Service.dll' `
  -WindowStyle Hidden `
  -RedirectStandardOutput '.\logs\service.out.log' `
  -RedirectStandardError '.\logs\service.err.log' `
  -PassThru
$p.Id | Tee-Object '.\logs\service.pid'
```

- 日志查看：
```powershell
Get-Content .\logs\service.out.log -Tail 60
Get-Content .\logs\service.err.log -Tail 60
```

## 停止服务
```powershell
# 安全操作（优先按 PID 文件，其次命令行匹配）
powershell -ExecutionPolicy Bypass -File .\scripts\kill-service.ps1
```

## 运行客户端（镜像输出到 UTF-8(BOM) 文件）
```powershell
# 准备 token 文件（开发态）
# 位置：C:\ProgramData\sys-sensor-v3\token （UTF-8）
# 内容：任意非空字符串

# 运行客户端（同时输出到控制台与 UTF-8(BOM) 日志文件）
dotnet run --project src\SystemMonitor.Client -c Debug -- --log .\logs\client.utf8.log

# 可选：设置基础推流间隔（单位 ms），例如 800ms
dotnet run --project src\SystemMonitor.Client -c Debug -- --base-interval 800 --log .\logs\client.utf8.log

# 可选：设置模块级间隔（name=ms;name=ms），例如 CPU 300ms、内存 1500ms
dotnet run --project src\SystemMonitor.Client -c Debug -- --module-intervals "cpu=300;memory=1500" --log .\logs\client.utf8.log

# 查看客户端日志
Get-Content .\logs\client.utf8.log -Tail 120
```

## Smoke Test 预期
- 客户端：
  - `hello` 返回 server_version、capabilities
  - `snapshot` 返回 cpu/memory/disk Mock 数据
  - `burst_subscribe(interval_ms=200, ttl_ms=3000)` 正常
  - 3.5 秒后打印：`收到 metrics 通知条数: <约 10~15>`，并显示最后一条 metrics JSON
  - `query_history(最近3秒, cpu/memory)` 返回 3 条 Mock 数据
- 服务端：
  - 日志出现 `hello / snapshot / burst_subscribe` 记录
  - 若设置了 `METRICS_LOG_EVERY=10`，出现 `metrics 推送累计: 10` 等计数信息

## 常见问题
- 中文乱码：
  - 客户端已设置 `Console.OutputEncoding = UTF8` 并提供 `--log` 将输出镜像到 UTF-8(BOM) 文件；请优先查看 `logs/client.utf8.log`
- 占用 DLL 无法重建：
  - 先运行 `scripts/kill-service.ps1` 结束后台服务后再构建

## JSON-RPC 方法（MVP）
- `hello(HelloParams)`：握手认证，返回服务能力
- `snapshot(SnapshotParams)`：即时快照（Mock）
- `burst_subscribe(BurstParams)`：临时提升推流频率
- `set_config(SetConfigParams)`：配置（已支持 `base_interval_ms` 基础速率；客户端也可通过 `--base-interval` 传入）
- `set_config(SetConfigParams)`：
  - `base_interval_ms`：基础推流间隔（最小 100ms）
  - `module_intervals`：模块级间隔字典（键不区分大小写，值最小 100ms）。当前支持模块：`cpu`、`memory`。
  - 推流间隔整形规则：在未处于 `burst_subscribe` 期间，使用 `min(base_interval_ms, min(module_intervals))`；处于 burst 期间，优先使用 burst 间隔。
  - 模块过滤：若设置了 `module_intervals`，推流仅包含配置过的模块字段；否则默认包含 `cpu` 与 `memory`。
- `start(StartParams)` / `stop()`：占位
- `query_history(QueryHistoryParams)`：历史查询（Mock）

## 调试技巧
- 调整推流频率：
  - 环境变量：`$env:METRICS_LOG_EVERY = 10`
  - 运行时：客户端调用 `burst_subscribe(interval_ms, ttl_ms)`
- 查看服务端运行状况：
  - `Get-Content .\logs\service.out.log -Tail 60`

---
以上步骤在 Windows 10 + .NET 8 环境验证通过。
