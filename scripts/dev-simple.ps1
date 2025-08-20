# 简化版开发脚本：避免复杂函数/大括号解析问题
# 用法：以管理员 PowerShell 运行  .\scripts\dev-simple.ps1

$ErrorActionPreference = 'Continue'

# 统一到仓库根目录
$root = (Resolve-Path (Join-Path $PSScriptRoot ".."))
Set-Location $root

Write-Host '[dev-simple] clean up residual processes'
# 清理常见子进程
Get-Process rustc,cargo,tauri,node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
# 直接杀掉可能存在的服务可执行与 dotnet 托管的同名进程
try { taskkill /F /T /IM SystemMonitor.Service.exe | Out-Null } catch { }
try {
  powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -like '*SystemMonitor.Service*' } | ForEach-Object { taskkill /PID $_.ProcessId /T /F }" | Out-Null
} catch { }

# 构建后端
Write-Host '[dev-simple] build backend'
& dotnet build src/SystemMonitor.Service/SystemMonitor.Service.csproj -c Debug -v minimal --nologo
if ($LASTEXITCODE -ne 0) { Write-Host '[dev-simple] build failed' -ForegroundColor Red; exit 1 }

# 启动后端（隐藏窗口）
Write-Host '[dev-simple] start backend: dotnet run'
$backend = Start-Process -FilePath 'dotnet' -ArgumentList @('run','-c','Debug','--project','src/SystemMonitor.Service/SystemMonitor.Service.csproj') -PassThru -WindowStyle Hidden

# 启动前端（Tauri Dev）
Write-Host '[dev-simple] start frontend'
Push-Location frontend
if (-not (Test-Path 'node_modules')) { npm install }
$frontend = Start-Process -FilePath 'npm' -ArgumentList @('run','tauri:dev') -PassThru
Pop-Location

Write-Host '[dev-simple] dev started. Close the Tauri window to stop.'
# 等待前端结束
if ($frontend) { Wait-Process -Id $frontend.Id }

# 清理
Write-Host '[dev-simple] cleaning up'
try { if ($frontend) { taskkill /PID $frontend.Id /T /F | Out-Null } } catch { }
try { if ($backend)  { taskkill /PID $backend.Id  /T /F | Out-Null } } catch { }
Get-Process rustc,cargo,tauri,node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Host '[dev-simple] done'
