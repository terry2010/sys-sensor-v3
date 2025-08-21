# sys-sensor-v3

系统监控服务与客户端（.NET 8）。服务通过 Windows 命名管道向前端/客户端提供 JSON-RPC 能力，管道名 `\\.\pipe\sys_sensor_v3.rpc`。

## 开发环境
- Windows 10/11
- .NET 8 SDK
- PowerShell（建议以管理员运行，便于管道 ACL）

## 构建
```powershell
dotnet build SysSensorV3.sln -c Debug -v minimal
```

## 方案 A：后台运行服务（类似 nohup）
特点：父终端关闭后仍运行；输出重定向到 `logs/`；PID 写入 `logs/service.pid`。

```powershell
New-Item -ItemType Directory -Force -Path .\logs | Out-Null
$p = Start-Process -FilePath 'dotnet' `
  -ArgumentList 'src\SystemMonitor.Service\bin\Debug\net8.0\SystemMonitor.Service.dll' `
  -WindowStyle Hidden `
  -RedirectStandardOutput '.\logs\service.out.log' `
  -RedirectStandardError '.\logs\service.err.log' `
  -PassThru
$p.Id | Tee-Object '.\logs\service.pid'
Start-Sleep -Seconds 2
```

查看日志：
```powershell
Get-Content .\logs\service.out.log -Wait
```

## 客户端 Smoke Test
```powershell
dotnet run --project src\SystemMonitor.Client -c Debug
# 或重定向到日志
dotnet run --project src\SystemMonitor.Client -c Debug *> .\logs\client.out.log
Get-Content .\logs\client.out.log
```

## 一键结束后台服务
我们提供了脚本 `scripts/kill-service.ps1`，会优先读取 `logs/service.pid` 杀进程；若不存在则按进程命令行匹配 `SystemMonitor.Service.dll/EXE` 强制结束。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\kill-service.ps1
# 仅查看将要结束哪些进程（dry run）
./scripts/kill-service.ps1 -VerboseOnly
```

手工命令（可替代脚本）：
```powershell
$svcPid = (Get-Content .\logs\service.pid -ErrorAction SilentlyContinue | Select-Object -First 1).Trim()
if ($svcPid) { taskkill /F /PID $svcPid; Remove-Item .\logs\service.pid -ErrorAction SilentlyContinue }
```

## 日志位置
- 服务：`logs/service.out.log`, `logs/service.err.log`
- 客户端：`logs/client.out.log`

## 常见问题
- dotnet run 随父控制台关闭：建议后台运行已编译 DLL（如上）。
- 出现 “所有的管道范例都在使用中”：服务端已加入指数退避与优雅断开（`RpcHostedService`），若仍出现请确认是否有异常退出或并发连接。

## 运行模式与命令行参数
- 默认以控制台模式运行。
- 使用 `--service` 参数可作为 Windows 服务运行（需以服务方式启动，见下文安装脚本）。

示例（控制台模式）：
```powershell
dotnet run --project src\SystemMonitor.Service -c Debug
```

示例（服务模式二进制直接启动，仅供调试）：
```powershell
src\SystemMonitor.Service\bin\Debug\net8.0\SystemMonitor.Service.exe --service
```

> 注意：正式以服务方式运行请使用安装脚本注册到 SCM，而非直接以 `--service` 前台方式运行。

### 命令行 --help 示例
借助 System.CommandLine 提供内置帮助：

```text
SystemMonitor.Service - Windows 系统监控服务

Usage:
  SystemMonitor.Service [options]

Options:
  --service        以 Windows 服务模式运行（用于服务托管场景）
  -?, -h, --help   显示帮助并退出
```

## Serilog 日志与热重载
- 配置文件：`src/SystemMonitor.Service/appsettings.json`
- 默认落盘：`logs/service-.log`（按日滚动，UTF-8）
- 支持通过修改 `appsettings.json` 动态调整日志级别（最小级别、覆盖规则等），无需重启。

示例（调整最小日志级别到 `Warning`）：
```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Warning"
    }
  }
}
```

实时查看：
```powershell
Get-Content .\logs\service.out.log -Wait
Get-Content .\logs\service.err.log -Wait
```

## 构建与打包脚本
我们提供 `scripts/build.ps1` 进行构建与可选打包，具备失败即停（fail-fast）。

常用示例：
```powershell
# Release 构建
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Configuration Release

# 指定框架（默认 net8.0）并打包 zip 到 artifacts/
./scripts/build.ps1 -Configuration Release -Framework net8.0 -Pack

# 清理 artifacts 目录
./scripts/build.ps1 -Clean
```

输出目录：`artifacts/` 下分别按项目名归档，例如 `artifacts/SystemMonitor.Service/`。

## 运行测试并收集结果
使用 `scripts/test.ps1` 运行全部测试并将 TRX 结果收集到 `artifacts/test-results/`。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1 -Configuration Release
Start-Process powershell -ArgumentList 'Get-ChildItem artifacts/test-results -Recurse'
```

### 测试报告生成与查看（TRX -> HTML）
我们提供 `scripts/trx-report.ps1` 将 TRX 转为 HTML 报告，输出到 `artifacts/test-results/report/index.html`。

推荐使用 PowerShell 7（pwsh）：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\trx-report.ps1 -TrxPath "artifacts\test-results\TestResults.trx" -Open
```

可选语言切换：使用 `-Lang`（默认 `en`，支持 `zh-CN`）。语言文案来自外置 JSON，路径：`scripts/trx-report.i18n.<lang>.json`。

示例（中文）：
```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\trx-report.ps1 -TrxPath "artifacts\test-results\TestResults.trx" -Lang zh-CN -Open
```

若使用 Windows PowerShell 5.1（powershell.exe），请确保脚本文件为 UTF-8 带 BOM，以避免中文或符号乱码：

```powershell
# 将脚本保存为 UTF-8 with BOM（一次性操作）
$p = 'scripts\trx-report.ps1'
$s = Get-Content -LiteralPath $p -Raw
$enc = New-Object System.Text.UTF8Encoding($true) # 含 BOM
[System.IO.File]::WriteAllText((Resolve-Path $p), $s, $enc)

# 运行报告生成
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\trx-report.ps1 -TrxPath "artifacts\test-results\TestResults.trx" -Open

# （可选）中文界面
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\trx-report.ps1 -TrxPath "artifacts\test-results\TestResults.trx" -Lang zh-CN -Open
```

注意：生成的 `index.html` 已使用 UTF-8 BOM，并在 `<head>` 中显式声明 `charset=utf-8`。

### i18n 文案文件
- 路径：`scripts/trx-report.i18n.en.json`, `scripts/trx-report.i18n.zh-CN.json`
- 编码：建议保存为 UTF-8 with BOM；脚本通过 .NET StreamReader 自动识别 BOM，并在无 BOM 时默认以 UTF-8 读取。
- 可扩展：若需新增语言，按 `scripts/trx-report.i18n.<lang>.json` 命名新增文件，并通过 `-Lang <lang>` 选择。

### 命令示例（生成并自动打开报告）
__PowerShell 7（pwsh）__
```powershell
# 英文
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\trx-report.ps1 `
  -TrxPath "artifacts\test-results\TestResults.trx" `
  -OutDir "artifacts/test-results/report-pwsh-en" `
  -Lang en -Open

# 中文
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\trx-report.ps1 `
  -TrxPath "artifacts\test-results\TestResults.trx" `
  -OutDir "artifacts/test-results/report-pwsh-zh" `
  -Lang zh-CN -Open
```

__Windows PowerShell 5.1（powershell.exe）__
```powershell
# 英文
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\trx-report.ps1 `
  -TrxPath "artifacts\test-results\TestResults.trx" `
  -OutDir "artifacts/test-results/report-ps51-en" `
  -Lang en -Open

# 中文
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\trx-report.ps1 `
  -TrxPath "artifacts\test-results\TestResults.trx" `
  -OutDir "artifacts/test-results/report-ps51-zh" `
  -Lang zh-CN -Open
```

## 安装为 Windows 服务（管理员）
使用 `scripts/install-service.ps1` 在本机注册/更新/启动服务。

```powershell
# 1) 构建后定位可执行文件路径（示例为 Release/net8.0）
$exe = Resolve-Path .\src\SystemMonitor.Service\bin\Release\net8.0\SystemMonitor.Service.exe

# 2) 安装/更新服务（需要管理员）
powershell -ExecutionPolicy Bypass -File .\scripts\install-service.ps1 -BinaryPath $exe

# 3) 启动/停止服务
./scripts/install-service.ps1 -Start
./scripts/install-service.ps1 -Stop

# 4) 删除服务
./scripts/install-service.ps1 -Remove
```

安装后日志仍输出到仓库根目录下的 `logs/`（若以服务账户运行，请确保目录权限可写，或在 `appsettings.json` 中调整路径）。

## API 参考与契约测试
- API 规范与示例：见 `doc/api-reference.md`
- 序列化策略：`snake_case`（`System.Text.Json` 自定义命名策略）
- 契约单测：`src/SystemMonitor.Tests/ContractTests.cs`，包含 `hello`/`set_config` 等请求体字段命名校验（正/负用例）

运行：
```powershell
./scripts/test.ps1
```

## 客户端快速试用（Smoke）
```powershell
dotnet run --project src\SystemMonitor.Client -c Debug
# 或重定向到日志
dotnet run --project src\SystemMonitor.Client -c Debug *> .\logs\client.out.log
Get-Content .\logs\client.out.log
```

## 前端（Tauri v2）事件桥配置与排障经验

__背景__

- 桌面端使用 Tauri v2 + Vue 3（目录 `frontend/`）。
- 后端（`SystemMonitor.Service`）通过 JSON-RPC 与前端通讯，并通过事件桥向前端持续推送 metrics。

__关键配置__

- 配置文件 `frontend/src-tauri/tauri.conf.json`
  - 在 `app` 下开启全局注入：`withGlobalTauri: true`，这样 DevTools 和前端都能使用 `window.__TAURI__`。
  - 不要在该文件中添加 `app.permissions` 或 `app.capabilities` 字段（Tauri v2 schema 不接受，可能导致 `tauri dev` 报错）。

- 能力文件 `frontend/src-tauri/capabilities/core-event.json`
  - 放置于 `src-tauri/capabilities/` 目录，Tauri CLI 会自动加载。
  - 示例：
    ```json
    {
      "identifier": "core-event",
      "description": "Allow listening to Tauri core events in dev.",
      "windows": ["*"],
      "webviews": ["*"],
      "permissions": [
        "core:event:default"
      ]
    }
    ```
  - 说明：`core:event:default` 已包含 `allow-listen`、`allow-unlisten`、`allow-emit`、`allow-emit-to` 等常用事件权限；通常无需再显式添加 `core:event:allow-listen`。

__后端调整__

- 文件 `src/SystemMonitor.Service/Services/RpcHostedService.cs`
  - 修正：不要在普通 RPC 调用里清空事件桥标记，避免误将桥连接当作短连。
  - 优化：`hello()` 握手成功后默认开启 metrics 推送订阅（避免前端订阅命令尚未到达时没有数据）。

__启动与验证__

- 启动（团队偏好，统一脚本）：
  ```powershell
  .\scripts\dev.ps1
  ```
- 在 Tauri 主窗口 DevTools Console 验证：
  ```js
  // 1) 确认全局可用
  typeof window.__TAURI__ === 'object'

  // 2) 建立事件桥（示例命令，按项目实际 RPC 命名为准）
  await window.__TAURI__.core.invoke('start_event_bridge')

  // 3) 监听桥事件
  await window.__TAURI__.event.listen('bridge_handshake', e => console.log('[bridge_handshake]', e))
  await window.__TAURI__.event.listen('bridge_rx', e => console.log('[bridge_rx]', e))

  // 4) 开启订阅并做一次 RPC 取数
  await window.__TAURI__.core.invoke('bridge_set_subscribe', { enable: true })
  await window.__TAURI__.core.invoke('rpc_call', { method: 'snapshot', params: {} }).then(console.log)
  ```
- 预期：前端 UI 的 `events`/`last`/`bridge_rx` 持续更新；后端日志可见握手与订阅开启。

__常见问题与排障__

- __DevTools 无法 import `@tauri-apps/api/...`__：在 DevTools 里请直接使用 `window.__TAURI__`，无需 `import`。
- __权限相关报错（not allowed to listen）__：确认 `core-event.json` 中的 `windows`/`webviews` 与 `permissions` 正确，通常 `core:event:default` 已足够；若仍报错，将完整错误贴出以便精确放权。
- __IDE 对 `permissions`/`capabilities` 的 schema 警告__：如果配置位于能力文件而非 `tauri.conf.json`，可忽略编辑器警告；以 Tauri CLI 的构建结果为准。

__经验总结__

- 使用能力文件集中声明权限，避免在 `tauri.conf.json` 里堆叠自定义字段。
- 打开 `withGlobalTauri` 便于调试与临时验证事件桥。
- 后端握手即订阅可降低“前端未及时订阅导致看不到数据”的问题。