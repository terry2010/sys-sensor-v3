# 简化开发环境启动脚本
Write-Host "=== SystemMonitor 开发环境启动脚本 ===" -ForegroundColor Cyan

# 设置工作目录
Set-Location "c:\code\sys-sensor-v3"

# 启动后端服务
Write-Host "1. 启动后端服务..." -ForegroundColor Yellow
Start-Process -FilePath "powershell.exe" -ArgumentList "-NoExit", "-Command", "cd 'c:\code\sys-sensor-v3\src\SystemMonitor.Service'; dotnet run -c Debug" -WindowStyle Normal

Write-Host "等待后端服务启动完成..." -ForegroundColor Gray
Start-Sleep -Seconds 5

# 检查后端服务是否启动
$backendProcess = Get-Process | Where-Object { $_.Name -eq "dotnet" -and $_.CommandLine -like "*SystemMonitor.Service*" }
if ($backendProcess) {
    Write-Host "后端服务启动成功! PID: $($backendProcess.Id)" -ForegroundColor Green
} else {
    Write-Host "后端服务启动可能失败，请检查控制台输出" -ForegroundColor Red
}

Write-Host "2. 启动前端应用..." -ForegroundColor Yellow
Start-Process -FilePath "powershell.exe" -ArgumentList "-NoExit", "-Command", "cd 'c:\code\sys-sensor-v3\frontend'; npm run tauri:dev" -WindowStyle Normal

Write-Host ""
Write-Host "=== 启动完成 ===" -ForegroundColor Cyan
Write-Host "请检查两个新打开的 PowerShell 窗口中的输出信息" -ForegroundColor Gray
Write-Host "要停止服务，请关闭对应的 PowerShell 窗口或使用 Ctrl+C" -ForegroundColor Gray