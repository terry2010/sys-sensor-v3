# SystemMonitor 修复验证脚本
Write-Host "=== SystemMonitor 修复验证脚本 ===" -ForegroundColor Cyan

# 1. 检查是否有重复进程
Write-Host "1. 检查SystemMonitor服务进程..." -ForegroundColor Yellow
$processes = Get-Process SystemMonitor.Service -ErrorAction SilentlyContinue
if ($processes) {
    Write-Host "发现 $($processes.Count) 个SystemMonitor.Service进程:" -ForegroundColor Gray
    $processes | ForEach-Object {
        $memory = [math]::Round($_.WorkingSet64/1MB, 1)
        Write-Host "  PID: $($_.Id), 内存: ${memory}MB, 启动时间: $($_.StartTime)" -ForegroundColor Gray
    }
    
    if ($processes.Count -gt 1) {
        Write-Host "警告: 发现多个服务进程，可能存在冲突" -ForegroundColor Yellow
    }
} else {
    Write-Host "未发现SystemMonitor.Service进程" -ForegroundColor Gray
}

# 2. 检查命名管道
Write-Host "`n2. 检查命名管道..." -ForegroundColor Yellow
$pipeExists = Test-Path "\\.\pipe\sys_sensor_v3.rpc"
if ($pipeExists) {
    Write-Host "✓ 命名管道存在: \\.\pipe\sys_sensor_v3.rpc" -ForegroundColor Green
} else {
    Write-Host "✗ 命名管道不存在" -ForegroundColor Red
}

# 3. 检查最近的日志
Write-Host "`n3. 检查最近日志..." -ForegroundColor Yellow
$logFile = "c:\code\sys-sensor-v3\logs\service-20250827.log"
if (Test-Path $logFile) {
    $recentLogLines = Get-Content $logFile -Tail 30
    $errors = $recentLogLines | Where-Object { $_ -like "*[ERR]*" }
    $warnings = $recentLogLines | Where-Object { $_ -like "*[WRN]*" }
    
    if ($errors) {
        Write-Host "发现 $($errors.Count) 个错误:" -ForegroundColor Red
        $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    } else {
        Write-Host "✓ 最近日志中无错误" -ForegroundColor Green
    }
    
    # 检查网络超时警告
    $networkTimeouts = $recentLogLines | Where-Object { $_ -like "*network 超时*" }
    if ($networkTimeouts) {
        Write-Host "发现 $($networkTimeouts.Count) 个网络超时警告:" -ForegroundColor Yellow
        $networkTimeouts | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }
    
    # 检查过载保护
    $overloadProtections = $recentLogLines | Where-Object { $_ -like "*过载保护*" }
    if ($overloadProtections) {
        Write-Host "发现 $($overloadProtections.Count) 个过载保护触发:" -ForegroundColor Yellow
        $overloadProtections | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }
} else {
    Write-Host "日志文件不存在: $logFile" -ForegroundColor Red
}

# 4. 检查编译输出
Write-Host "`n4. 检查编译输出..." -ForegroundColor Yellow
$exePath = "c:\code\sys-sensor-v3\src\SystemMonitor.Service\bin\Release\net8.0\SystemMonitor.Service.exe"
if (Test-Path $exePath) {
    $fileInfo = Get-Item $exePath
    Write-Host "✓ 服务可执行文件存在，大小: $([math]::Round($fileInfo.Length/1KB, 1))KB，修改时间: $($fileInfo.LastWriteTime)" -ForegroundColor Green
} else {
    Write-Host "✗ 服务可执行文件不存在" -ForegroundColor Red
}

# 5. 性能计数器检查
Write-Host "`n5. 检查性能计数器..." -ForegroundColor Yellow
try {
    $gpuCounters = Get-Counter -ListSet "GPU Engine" -ErrorAction SilentlyContinue
    if ($gpuCounters) {
        Write-Host "✓ GPU性能计数器可用" -ForegroundColor Green
    } else {
        Write-Host "? GPU性能计数器不可用（可能需要安装显卡驱动）" -ForegroundColor Yellow
    }
} catch {
    Write-Host "? 无法检查GPU性能计数器: $($_.Exception.Message)" -ForegroundColor Yellow
}

try {
    $networkCounters = Get-Counter -ListSet "Network Interface" -ErrorAction SilentlyContinue
    if ($networkCounters) {
        Write-Host "✓ 网络接口性能计数器可用" -ForegroundColor Green
    } else {
        Write-Host "✗ 网络接口性能计数器不可用" -ForegroundColor Red
    }
} catch {
    Write-Host "? 无法检查网络接口性能计数器: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host "`n=== 验证完成 ===" -ForegroundColor Cyan
Write-Host "如果所有检查都通过，系统应该能正常运行" -ForegroundColor Gray
Write-Host "如果仍有问题，请检查日志文件获取详细信息" -ForegroundColor Gray