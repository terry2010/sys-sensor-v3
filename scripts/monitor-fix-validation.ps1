# SystemMonitor Fix Validation Script
# Monitor the effectiveness of overload protection and emergency brake mechanism

Write-Host "=== SystemMonitor Fix Validation Monitor ===" -ForegroundColor Cyan
Write-Host "Monitoring overload protection effectiveness and system stability" -ForegroundColor Yellow

$monitorDuration = 300  # Monitor for 5 minutes 
$checkInterval = 15     # Check every 15 seconds
$endTime = (Get-Date).AddSeconds($monitorDuration)

Write-Host "Monitor Duration: $monitorDuration seconds (5 minutes)" -ForegroundColor Yellow
Write-Host "Check Interval: $checkInterval seconds" -ForegroundColor Yellow
Write-Host "Start Time: $(Get-Date)" -ForegroundColor Green
Write-Host ""

$checkCount = 0
$crashes = 0
$lastPid = $null
$overloadProtections = 0
$emergencyBrakes = 0
$maxMemory = 0
$maxHandles = 0
$maxThreads = 0

while ((Get-Date) -lt $endTime) {
    $checkCount++
    
    Write-Host "[$checkCount] $(Get-Date -Format 'HH:mm:ss')" -ForegroundColor White
    
    # Check if service is running
    $serviceProc = Get-Process SystemMonitor.Service -ErrorAction SilentlyContinue
    
    if ($serviceProc) {
        $servicePid = $serviceProc.Id
        $memoryMB = [math]::Round($serviceProc.WorkingSet64/1MB, 1)
        $handles = $serviceProc.HandleCount
        $threads = $serviceProc.Threads.Count
        $uptime = (Get-Date) - $serviceProc.StartTime
        
        # Track maximum resource usage
        $maxMemory = [math]::Max($maxMemory, $memoryMB)
        $maxHandles = [math]::Max($maxHandles, $handles)
        $maxThreads = [math]::Max($maxThreads, $threads)
        
        # Check if PID changed (crash detection)
        if ($lastPid -ne $null -and $lastPid -ne $servicePid) {
            $crashes++
            Write-Host "  CRASH DETECTED! Process restart detected! Old PID: $lastPid, New PID: $servicePid" -ForegroundColor Red
        }
        $lastPid = $servicePid
        
        Write-Host "  Service running - PID: $servicePid" -ForegroundColor Green
        Write-Host "  Memory: ${memoryMB}MB (max: ${maxMemory}MB) | Handles: $handles (max: ${maxHandles}) | Threads: $threads (max: ${maxThreads})" -ForegroundColor Gray
        Write-Host "  Uptime: $([int]$uptime.TotalMinutes)m$([int]$uptime.Seconds)s" -ForegroundColor Gray
        
        # Check for resource warnings
        $warnings = @()
        if ($memoryMB -gt 200) { $warnings += "High memory: ${memoryMB}MB" }
        if ($handles -gt 1000) { $warnings += "High handles: $handles" }
        if ($threads -gt 40) { $warnings += "High threads: $threads" }
        
        if ($warnings.Count -gt 0) {
            Write-Host "  WARNING: $($warnings -join ', ')" -ForegroundColor Yellow
        }
        
        # Check recent log for overload protections
        try {
            $recentLogs = Get-Content "c:\code\sys-sensor-v3\logs\service-20250826_001.log" -Tail 50 -ErrorAction SilentlyContinue
            if ($recentLogs) {
                $newOverloadProtections = ($recentLogs | Where-Object { $_ -like "*检测到系统过载*" }).Count
                $newEmergencyBrakes = ($recentLogs | Where-Object { $_ -like "*紧急刹车*" -or $_ -like "*连续*周期性能不佳*" }).Count
                
                if ($newOverloadProtections -gt $overloadProtections) {
                    $diff = $newOverloadProtections - $overloadProtections
                    $overloadProtections = $newOverloadProtections
                    Write-Host "  OVERLOAD PROTECTION: +$diff times (total: $overloadProtections)" -ForegroundColor Magenta
                }
                
                if ($newEmergencyBrakes -gt $emergencyBrakes) {
                    $diff = $newEmergencyBrakes - $emergencyBrakes
                    $emergencyBrakes = $newEmergencyBrakes
                    Write-Host "  EMERGENCY BRAKE: +$diff times (total: $emergencyBrakes)" -ForegroundColor Red
                }
            }
        } catch {
            Write-Host "  Note: Could not check log file" -ForegroundColor DarkGray
        }
        
    } else {
        $crashes++
        Write-Host "  ERROR: Service not running!" -ForegroundColor Red
        $lastPid = $null
    }
    
    Write-Host ""
    Start-Sleep -Seconds $checkInterval
}

Write-Host "=== Fix Validation Complete ===" -ForegroundColor Cyan
Write-Host "Monitoring Summary:" -ForegroundColor White
Write-Host "  Total checks: $checkCount" -ForegroundColor White
Write-Host "  Service crashes: $crashes" -ForegroundColor $(if ($crashes -gt 0) { "Red" } else { "Green" })
Write-Host "  Overload protections triggered: $overloadProtections" -ForegroundColor Magenta
Write-Host "  Emergency brakes triggered: $emergencyBrakes" -ForegroundColor $(if ($emergencyBrakes -gt 0) { "Red" } else { "Green" })
Write-Host "  Peak memory usage: ${maxMemory}MB" -ForegroundColor White
Write-Host "  Peak handle count: $maxHandles" -ForegroundColor White  
Write-Host "  Peak thread count: $maxThreads" -ForegroundColor White
Write-Host "End time: $(Get-Date)" -ForegroundColor Green

# Analysis
Write-Host "`n=== Analysis ===" -ForegroundColor Cyan
if ($crashes -eq 0) {
    Write-Host "✓ SUCCESS: No service crashes detected during monitoring period" -ForegroundColor Green
} else {
    Write-Host "✗ FAILURE: $crashes service crashes occurred" -ForegroundColor Red
}

if ($overloadProtections -gt 0) {
    Write-Host "✓ PROTECTION: Overload protection mechanism is working ($overloadProtections activations)" -ForegroundColor Green
} else {
    Write-Host "? INFO: No overload protections triggered (system may be running well)" -ForegroundColor Yellow
}

if ($maxMemory -lt 200 -and $maxHandles -lt 1000 -and $maxThreads -lt 40) {
    Write-Host "✓ RESOURCE HEALTH: Resource usage remained within healthy limits" -ForegroundColor Green
} else {
    Write-Host "! CAUTION: Resource usage approached warning levels" -ForegroundColor Yellow
}