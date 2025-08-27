# 启动SystemMonitor服务脚本
Write-Host "=== SystemMonitor Service Start Script ===" -ForegroundColor Cyan

# 检查是否已有进程在运行
$existingProcesses = Get-Process SystemMonitor.Service -ErrorAction SilentlyContinue
if ($existingProcesses) {
    Write-Host "发现正在运行的SystemMonitor.Service进程，正在停止..." -ForegroundColor Yellow
    $existingProcesses | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# 检查命名管道是否还存在
$pipeExists = Test-Path "\\.\pipe\sys_sensor_v3.rpc"
if ($pipeExists) {
    Write-Host "发现残留的命名管道，可能需要等待系统清理..." -ForegroundColor Yellow
    Start-Sleep -Seconds 3
}

# 启动服务
Write-Host "正在启动SystemMonitor服务..." -ForegroundColor Green
# 检查Release目录是否存在
if (Test-Path "c:\code\sys-sensor-v3\src\SystemMonitor.Service\bin\Release\net8.0\SystemMonitor.Service.exe") {
    Set-Location "c:\code\sys-sensor-v3\src\SystemMonitor.Service\bin\Release\net8.0"
    Start-Process -FilePath ".\SystemMonitor.Service.exe" -WindowStyle Normal
}
# 如果Release目录不存在，尝试Debug目录
elseif (Test-Path "c:\code\sys-sensor-v3\src\SystemMonitor.Service\bin\Debug\net8.0\SystemMonitor.Service.exe") {
    Set-Location "c:\code\sys-sensor-v3\src\SystemMonitor.Service\bin\Debug\net8.0"
    Start-Process -FilePath ".\SystemMonitor.Service.exe" -WindowStyle Normal
}
# 如果都不存在，使用dotnet run
else {
    Write-Host "未找到预编译的可执行文件，使用dotnet run启动..." -ForegroundColor Yellow
    Set-Location "c:\code\sys-sensor-v3\src\SystemMonitor.Service"
    Start-Process -FilePath "dotnet" -ArgumentList "run" -WindowStyle Normal
}

Write-Host "服务启动命令已发送，等待服务初始化..." -ForegroundColor Green
Start-Sleep -Seconds 5

# 检查服务是否成功启动
$serviceProcess = Get-Process SystemMonitor.Service -ErrorAction SilentlyContinue
if ($serviceProcess) {
    $pid = $serviceProcess.Id
    $memory = [math]::Round($serviceProcess.WorkingSet64/1MB, 1)
    Write-Host "服务启动成功! PID: $pid, 内存占用: ${memory}MB" -ForegroundColor Green
} else {
    Write-Host "服务启动失败，请检查日志文件" -ForegroundColor Red
}

Write-Host "=== 启动脚本执行完成 ===" -ForegroundColor Cyan