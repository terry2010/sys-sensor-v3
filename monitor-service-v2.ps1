# SystemMonitor Service Process Monitor
# Monitors SystemMonitor.Service process for crashes and exits

param(
    [string]$ServiceExe = "SystemMonitor.Service.exe",
    [string]$LogFile = "service-monitor.log",
    [int]$CheckIntervalSeconds = 5
)

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $logEntry = "[$timestamp] $Message"
    Write-Host $logEntry
    Add-Content -Path $LogFile -Value $logEntry -Encoding UTF8
}

Write-Log "=== SystemMonitor Service Monitoring Started ==="
Write-Log "Target: $ServiceExe"
Write-Log "Check Interval: $CheckIntervalSeconds seconds"
Write-Log "Log File: $LogFile"

$lastProcessId = $null
$processStartTime = $null
$checkCount = 0

while ($true) {
    $checkCount++
    
    try {
        # Find SystemMonitor service process
        $processes = Get-Process -Name "SystemMonitor.Service" -ErrorAction SilentlyContinue
        
        if ($processes) {
            $process = $processes[0]  # Take first process
            $currentProcessId = $process.Id
            
            # Check if this is a new process
            if ($lastProcessId -ne $currentProcessId) {
                if ($lastProcessId) {
                    Write-Log "Process change detected: Old PID=$lastProcessId, New PID=$currentProcessId"
                    $crashDuration = (Get-Date) - $processStartTime
                    Write-Log "Previous process run duration: $($crashDuration.ToString())"
                    
                    # Check Windows Event Log for related errors
                    try {
                        $recentErrors = Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2; StartTime=(Get-Date).AddMinutes(-5)} -MaxEvents 10 -ErrorAction SilentlyContinue |
                            Where-Object { $_.ProviderName -like "*SystemMonitor*" -or $_.Message -like "*SystemMonitor*" }
                        
                        if ($recentErrors) {
                            Write-Log "Found related error logs:"
                            foreach ($error in $recentErrors) {
                                Write-Log "  [Event Log] $($error.TimeCreated): $($error.LevelDisplayName) - $($error.Message)"
                            }
                        }
                    } catch {
                        Write-Log "Cannot read event log: $($_.Exception.Message)"
                    }
                }
                
                $lastProcessId = $currentProcessId
                $processStartTime = $process.StartTime
                Write-Log "Monitoring new process: PID=$currentProcessId, Start Time=$($process.StartTime), Memory=$([math]::Round($process.WorkingSet64/1MB,2))MB"
            }
            
            # Record process health status (every minute)
            if ($checkCount % (60 / $CheckIntervalSeconds) -eq 0) {
                $memoryMB = [math]::Round($process.WorkingSet64 / 1MB, 2)
                $handleCount = $process.HandleCount
                $threadCount = $process.Threads.Count
                $runTime = (Get-Date) - $process.StartTime
                
                Write-Log "Process health check: PID=$($process.Id), Run Time=$($runTime.ToString()), Memory=${memoryMB}MB, Handles=$handleCount, Threads=$threadCount"
                
                # Check for resource usage anomalies
                if ($memoryMB -gt 500) {
                    Write-Log "[WARNING] High memory usage: ${memoryMB}MB"
                }
                if ($handleCount -gt 1000) {
                    Write-Log "[WARNING] High handle count: $handleCount"
                }
            }
            
        } else {
            if ($lastProcessId) {
                Write-Log "[CRITICAL] Process disappeared! Last PID=$lastProcessId"
                $crashDuration = (Get-Date) - $processStartTime
                Write-Log "Process run duration: $($crashDuration.ToString())"
                
                # Try to find crash cause
                Write-Log "Searching for crash cause..."
                
                # Check recent system errors
                try {
                    $systemErrors = Get-WinEvent -FilterHashtable @{LogName='System'; Level=1,2,3; StartTime=(Get-Date).AddMinutes(-2)} -MaxEvents 5 -ErrorAction SilentlyContinue
                    if ($systemErrors) {
                        Write-Log "Found system errors:"
                        foreach ($error in $systemErrors) {
                            Write-Log "  [System] $($error.TimeCreated): $($error.LevelDisplayName) - $($error.Message)"
                        }
                    }
                } catch {}
                
                # Check application errors
                try {
                    $appErrors = Get-WinEvent -FilterHashtable @{LogName='Application'; Level=1,2; StartTime=(Get-Date).AddMinutes(-2)} -MaxEvents 10 -ErrorAction SilentlyContinue
                    if ($appErrors) {
                        Write-Log "Found application errors:"
                        foreach ($error in $appErrors) {
                            Write-Log "  [Application] $($error.TimeCreated): $($error.LevelDisplayName) - $($error.Message)"
                        }
                    }
                } catch {}
                
                $lastProcessId = $null
                $processStartTime = $null
            } else {
                Write-Log "SystemMonitor.Service process not found (check #$checkCount)"
            }
        }
        
    } catch {
        Write-Log "[ERROR] Monitoring exception: $($_.Exception.Message)"
    }
    
    Start-Sleep -Seconds $CheckIntervalSeconds
}