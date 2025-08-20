# 运维与调试备注（M1 骨架）

## 1. 常用命令（PowerShell，Windows 10）
- 运行端到端测试：
  ```powershell
  scripts\test.ps1
  ```
- 构建与本地运行服务（控制台）：
  ```powershell
  dotnet build -c Release
  dotnet run -c Release --project .\src\SystemMonitor.Service\SystemMonitor.Service.csproj
  ```
- 终止残留服务进程：
  ```powershell
  scripts\kill-service.ps1
  ```
- 查看日志（服务）：
  ```powershell
  Get-Content -Path .\logs\service*.log -Tail 200 -Wait
  ```

## 2. 命名管道排障
- 管道名：`\\.\pipe\sys_sensor_v3.rpc`
- 若连接超时：
  - 检查是否存在残留 `SystemMonitor.Service` 进程
  - 查看 `logs/service-*.log` 是否有启动异常
  - 确认当前用户在管道 ACL 允许列表

## 3. 日志级别与定位
- 日志位置：`logs/`
- 统一结构化输出，注意不记录 Token/PII
- 测试失败时，测试框架会附带服务控制台与 Serilog 文件尾部

## 4. 脚本说明
- `scripts/build.ps1`：构建解决方案并输出到 `artifacts/`
- `scripts/test.ps1`：运行测试，结果输出到 `artifacts/test-results/`
- `scripts/kill-service.ps1`：安全结束残留服务进程
- `scripts/install-service.ps1`：安装为 Windows 服务（如启用）

## 5. 已知限制（M1）
- 仅支持 Windows，依赖 Named Pipe 与部分 Win32 API
- 协议版本 `protocol_version=1`，不保证前向兼容
- 更新/自更新能力不在 M1 范围
