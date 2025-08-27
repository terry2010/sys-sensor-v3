# SystemMonitor服务监控脚本
Write-Host "=== SystemMonitor Service Monitor ===" -ForegroundColor Cyan

$checkInterval = 10  # 每10秒检查一次
$maxChecks = 180     # 最多检查180次（30分钟）

for ($i = 1; $i -le $maxChecks; $i++) {
    Write-Host "[$i] $(Get-Date -Format 'HH:mm:ss')" -ForegroundColor White
    
    # 检查服务进程
    $serviceProcess = Get-Process SystemMonitor.Service -ErrorAction SilentlyContinue
    if ($serviceProcess) {
        $pid = $serviceProcess.Id
        $memory = [math]::Round($serviceProcess.WorkingSet64/1MB, 1)
        $handles = $serviceProcess.HandleCount
        $threads = $serviceProcess.Threads.Count
        $uptime = (Get-Date) - $serviceProcess.StartTime
        
        Write-Host "  服务运行中 - PID: $pid" -ForegroundColor Green
        Write-Host "  内存: ${memory}MB | 句柄: $handles | 线程: $threads" -ForegroundColor Gray
        Write-Host "  运行时间: $([int]$uptime.TotalMinutes)m$([int]$uptime.Seconds)s" -ForegroundColor Gray
        
        # 检查资源使用是否过高
        $warnings = @()
        if ($memory -gt 300) { $warnings += "高内存使用: ${memory}MB" }
        if ($handles -gt 1500) { $warnings += "高句柄数: $handles" }
        if ($threads -gt 50) { $warnings += "高线程数: $threads" }
        
        if ($warnings.Count -gt 0) {
            Write-Host "  警告: $($warnings -join ', ')" -ForegroundColor Yellow
        }
        
        # 检查最近的日志是否有错误
        $logFile = "c:\code\sys-sensor-v3\logs\service-20250827.log"
        if (Test-Path $logFile) {
            $recentErrors = Get-Content $logFile -Tail 20 | Where-Object { $_ -like "*[ERR]*" -or $_ -like "*[WRN]*" }
            if ($recentErrors) {
                Write-Host "  最近日志中有警告或错误信息:" -ForegroundColor Yellow
                $recentErrors | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
            }
        }
    } else {
        Write-Host "  错误: SystemMonitor服务未运行!" -ForegroundColor Red
        Write-Host "  尝试重新启动服务..." -ForegroundColor Yellow
        
        # 尝试重新启动服务
        Set-Location "c:\code\sys-sensor-v3\src\SystemMonitor.Service\bin\Release\net8.0\win-x64"
        Start-Process -FilePath ".\SystemMonitor.Service.exe" -WindowStyle Normal
        Start-Sleep -Seconds 3
    }
    
    Write-Host ""
    Start-Sleep -Seconds $checkInterval
}

Write-Host "=== 监控结束 ===" -ForegroundColor Cyan