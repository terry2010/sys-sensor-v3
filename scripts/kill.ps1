# SystemMonitor Project Process Cleanup Script
# Safe version: will not kill system processes or editors

param(
    [switch]$Verbose = $false
)

function Write-Log {
    param([string]$Message, [string]$Color = "White")
    Write-Host "[kill] $Message" -ForegroundColor $Color
}

Write-Log "Starting cleanup of SystemMonitor related processes..." "Cyan"

# Get current process ID to avoid suicide
$currentPid = $PID
Write-Log "Current script process ID: $currentPid" "Gray"

# 1. Kill SystemMonitor.Service processes
try {
    $procs = Get-Process -Name "SystemMonitor.Service" -ErrorAction SilentlyContinue
    if ($procs) {
        Write-Log "Found SystemMonitor.Service processes:" "Yellow"
        foreach ($proc in $procs) {
            if ($proc.Id -ne $currentPid) {
                try {
                    # Check if process has already exited
                    if (!$proc.HasExited) {
                        $proc.Kill()
                        Write-Log "Killed PID: $($proc.Id)" "Green"
                    } else {
                        if ($Verbose) { Write-Log "Process PID: $($proc.Id) has already exited" "Gray" }
                    }
                } catch {
                    Write-Log "Unable to kill PID: $($proc.Id) - $($_.Exception.Message)" "Red"
                }
            }
        }
    } else {
        if ($Verbose) { Write-Log "No SystemMonitor.Service processes found" "Gray" }
    }
} catch {
    Write-Log "SystemMonitor.Service check error: $($_.Exception.Message)" "Red"
}

# 2. Kill related dotnet processes
try {
    $dotnetProcs = Get-Process dotnet -ErrorAction SilentlyContinue
    if ($dotnetProcs) {
        Write-Log "Checking dotnet processes..." "Yellow"
        $killedAny = $false
        foreach ($proc in $dotnetProcs) {
            if ($proc.Id -ne $currentPid) {
                # Simple check: if it's a recent dotnet process within our scope
                try {
                    $null = $proc.WorkingSet  # 触发可能的访问异常
                    # Check if process has already exited
                    if (!$proc.HasExited) {
                        $proc.Kill()
                        Write-Log "Killed dotnet PID: $($proc.Id)" "Green"
                        $killedAny = $true
                    } else {
                        if ($Verbose) { Write-Log "Dotnet process PID: $($proc.Id) has already exited" "Gray" }
                    }
                } catch {
                    # Ignore inaccessible processes
                }
            }
        }
        if (-not $killedAny -and $Verbose) {
            Write-Log "No dotnet processes need cleanup" "Gray"
        }
    } else {
        if ($Verbose) { Write-Log "No dotnet processes found" "Gray" }
    }
} catch {
    Write-Log "dotnet process check error: $($_.Exception.Message)" "Red"
}

# 3. Kill Tauri related processes
try {
    $tauriProcs = Get-Process | Where-Object { 
        $_.ProcessName -like "*sys-sensor-v3*" -or $_.ProcessName -like "*tauri*"
    }
    
    if ($tauriProcs) {
        Write-Log "Found Tauri related processes:" "Yellow"
        foreach ($proc in $tauriProcs) {
            if ($proc.Id -ne $currentPid) {
                try {
                    # Check if process has already exited
                    if (!$proc.HasExited) {
                        $proc.Kill()
                        Write-Log "Killed Tauri PID: $($proc.Id)" "Green"
                    } else {
                        if ($Verbose) { Write-Log "Tauri process PID: $($proc.Id) has already exited" "Gray" }
                    }
                } catch {
                    Write-Log "Unable to kill Tauri PID: $($proc.Id) - $($_.Exception.Message)" "Red"
                }
            }
        }
    } else {
        if ($Verbose) { Write-Log "No Tauri related processes found" "Gray" }
    }
} catch {
    Write-Log "Tauri process check error: $($_.Exception.Message)" "Red"
}

# 4. Kill related Node.js processes by port
$devPorts = @(1420, 3000, 5000, 5173, 8080)
Write-Log "Checking development port usage..." "Yellow"

foreach ($port in $devPorts) {
    try {
        $connections = netstat -ano | Select-String ":$port "
        foreach ($conn in $connections) {
            if ($conn -match "\s+(\d+)$") {
                $pid = [int]$matches[1]
                if ($pid -ne $currentPid) {
                    try {
                        $proc = Get-Process -Id $pid -ErrorAction SilentlyContinue
                        if ($proc -and ($proc.ProcessName -like "*node*" -or $proc.ProcessName -like "*tauri*")) {
                            $proc.Kill()
                            Write-Log "Killed process occupying port $port, PID: $pid" "Green"
                        }
                    } catch {
                        # Ignore processes that cannot be killed
                    }
                }
            }
        }
    } catch {
        # Ignore netstat errors
    }
}

Write-Log "Process cleanup completed" "Cyan"

# Wait a moment for processes to fully exit
Start-Sleep -Milliseconds 500

Write-Log "Cleanup finished" "Green"