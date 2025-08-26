# SystemMonitor Service Health Monitor

Write-Host "=== SystemMonitor Service Health Monitor ===" -ForegroundColor Cyan

$monitorDuration = 120  # Monitor for 2 minutes
$checkInterval = 10     # Check every 10 seconds
$endTime = (Get-Date).AddSeconds($monitorDuration)

Write-Host "Monitor Duration: $monitorDuration seconds" -ForegroundColor Yellow
Write-Host "Check Interval: $checkInterval seconds" -ForegroundColor Yellow
Write-Host "Start Time: $(Get-Date)" -ForegroundColor Green
Write-Host ""

$checkCount = 0
$crashes = 0
$lastPid = $null

while ((Get-Date) -lt $endTime) {
    $checkCount++
    
    Write-Host "[$checkCount] $(Get-Date -Format 'HH:mm:ss')" -ForegroundColor White
    
    # Check if service is running
    $serviceProc = Get-Process SystemMonitor.Service -ErrorAction SilentlyContinue
    
    if ($serviceProc) {
        $pid = $serviceProc.Id
        $memoryMB = [math]::Round($serviceProc.WorkingSet64/1MB, 1)
        $handles = $serviceProc.HandleCount
        $threads = $serviceProc.Threads.Count
        $uptime = (Get-Date) - $serviceProc.StartTime
        
        # Check if PID changed
        if ($lastPid -ne $null -and $lastPid -ne $pid) {
            $crashes++
            Write-Host "  WARNING: Process restart detected! Old PID: $lastPid, New PID: $pid" -ForegroundColor Red
        }
        $lastPid = $pid
        
        Write-Host "  Service running - PID: $pid" -ForegroundColor Green
        Write-Host "  Memory: ${memoryMB}MB | Handles: $handles | Threads: $threads" -ForegroundColor Gray
        Write-Host "  Uptime: $([int]$uptime.TotalMinutes)m$([int]$uptime.Seconds)s" -ForegroundColor Gray
        
        # Check for warning signs
        if ($memoryMB -gt 150) {
            Write-Host "  WARNING: High memory usage: ${memoryMB}MB" -ForegroundColor Yellow
        }
        if ($handles -gt 800) {
            Write-Host "  WARNING: High handle count: $handles" -ForegroundColor Yellow
        }
        if ($threads -gt 25) {
            Write-Host "  WARNING: High thread count: $threads" -ForegroundColor Yellow
        }
    } else {
        $crashes++
        Write-Host "  ERROR: Service not running!" -ForegroundColor Red
        $lastPid = $null
    }
    
    Write-Host ""
    Start-Sleep -Seconds $checkInterval
}

Write-Host "=== Monitoring Complete ===" -ForegroundColor Cyan
Write-Host "Total checks: $checkCount" -ForegroundColor White
Write-Host "Crashes detected: $crashes" -ForegroundColor $(if ($crashes -gt 0) { "Red" } else { "Green" })
Write-Host "End time: $(Get-Date)" -ForegroundColor Green