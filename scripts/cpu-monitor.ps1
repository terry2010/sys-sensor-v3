# SystemMonitor CPU Usage Monitor Script
# Monitor the CPU usage of the SystemMonitor service process

Write-Host "=== SystemMonitor CPU Usage Monitor ===" -ForegroundColor Cyan
Write-Host "Monitoring CPU usage of SystemMonitor.Service process" -ForegroundColor Yellow

$monitorDuration = 300  # Monitor for 5 minutes 
$checkInterval = 5      # Check every 5 seconds
$endTime = (Get-Date).AddSeconds($monitorDuration)

Write-Host "Monitor Duration: $monitorDuration seconds (5 minutes)" -ForegroundColor Yellow
Write-Host "Check Interval: $checkInterval seconds" -ForegroundColor Yellow
Write-Host "Start Time: $(Get-Date)" -ForegroundColor Green
Write-Host ""

$checkCount = 0
$cpuSamples = @()
$memorySamples = @()
$processId = $null

while ((Get-Date) -lt $endTime) {
    $checkCount++
    
    Write-Host "[$checkCount] $(Get-Date -Format 'HH:mm:ss')" -ForegroundColor White
    
    # Check if service is running
    $serviceProc = Get-Process SystemMonitor.Service -ErrorAction SilentlyContinue
    
    if ($serviceProc) {
        $processId = $serviceProc.Id
        $cpuUsage = [math]::Round($serviceProc.CPU, 2)
        $memoryMB = [math]::Round($serviceProc.WorkingSet64/1MB, 1)
        $handles = $serviceProc.HandleCount
        $threads = $serviceProc.Threads.Count
        $uptime = (Get-Date) - $serviceProc.StartTime
        
        # Store samples for statistics
        $cpuSamples += $cpuUsage
        $memorySamples += $memoryMB
        
        Write-Host "  Service running - PID: $processId" -ForegroundColor Green
        Write-Host "  CPU Usage: $cpuUsage% | Memory: ${memoryMB}MB" -ForegroundColor Gray
        Write-Host "  Handles: $handles | Threads: $threads" -ForegroundColor Gray
        Write-Host "  Uptime: $([int]$uptime.TotalMinutes)m$([int]$uptime.Seconds)s" -ForegroundColor Gray
        
        # Check for high CPU usage
        if ($cpuUsage -gt 20) {
            Write-Host "  WARNING: High CPU usage detected: $cpuUsage%" -ForegroundColor Yellow
        }
        
        # Check for high memory usage
        if ($memoryMB -gt 200) {
            Write-Host "  WARNING: High memory usage detected: ${memoryMB}MB" -ForegroundColor Yellow
        }
        
    } else {
        Write-Host "  INFO: Service not running or not found" -ForegroundColor DarkGray
    }
    
    Write-Host ""
    Start-Sleep -Seconds $checkInterval
}

# Calculate statistics
if ($cpuSamples.Count -gt 0) {
    $avgCpu = [math]::Round(($cpuSamples | Measure-Object -Average).Average, 2)
    $maxCpu = [math]::Round(($cpuSamples | Measure-Object -Maximum).Maximum, 2)
    $minCpu = [math]::Round(($cpuSamples | Measure-Object -Minimum).Minimum, 2)
    
    $avgMemory = [math]::Round(($memorySamples | Measure-Object -Average).Average, 1)
    $maxMemory = [math]::Round(($memorySamples | Measure-Object -Maximum).Maximum, 1)
    
    Write-Host "=== CPU Usage Statistics ===" -ForegroundColor Cyan
    Write-Host "Total samples: $($cpuSamples.Count)" -ForegroundColor White
    Write-Host "Average CPU usage: $avgCpu%" -ForegroundColor White
    Write-Host "Maximum CPU usage: $maxCpu%" -ForegroundColor White
    Write-Host "Minimum CPU usage: $minCpu%" -ForegroundColor White
    Write-Host "Average memory usage: ${avgMemory}MB" -ForegroundColor White
    Write-Host "Maximum memory usage: ${maxMemory}MB" -ForegroundColor White
    
    # Analysis
    Write-Host "`n=== Analysis ===" -ForegroundColor Cyan
    if ($avgCpu -lt 5) {
        Write-Host "✓ CPU usage is low - system is efficient" -ForegroundColor Green
    } elseif ($avgCpu -lt 15) {
        Write-Host "⚠ CPU usage is moderate - system is functioning normally" -ForegroundColor Yellow
    } else {
        Write-Host "⚠⚠ High CPU usage detected - system may be under stress" -ForegroundColor Red
    }
    
    if ($maxCpu -gt 30) {
        Write-Host "⚠ Peak CPU usage reached $maxCpu% - check for performance bottlenecks" -ForegroundColor Yellow
    }
} else {
    Write-Host "No data collected - service may not have been running" -ForegroundColor Red
}

Write-Host "`nEnd time: $(Get-Date)" -ForegroundColor Green