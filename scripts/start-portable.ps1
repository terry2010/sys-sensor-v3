# This script starts the backend service and the frontend app, and cleans up processes on exit

param(
    [switch]$NoKillExisting = $false
)

$ErrorActionPreference = "Continue"

function Write-Log {
    param([string]$Message, [string]$Color = "White")
    Write-Host "[portable-start] $Message" -ForegroundColor $Color
}

# Locate script directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Write-Log "Script directory: $scriptDir" "Gray"

# Global variables to store started processes
$global:BackendProcess = $null
$global:FrontendProcess = $null

# Cleanup function
function Cleanup-AllProcesses {
    Write-Log "Cleaning up all started processes..." "Yellow"
    
    # Cleanup frontend process
    try {
        if ($global:FrontendProcess -and !$global:FrontendProcess.HasExited) {
            Write-Log "Stopping frontend process (PID: $($global:FrontendProcess.Id))..."
            Stop-Process -Id $global:FrontendProcess.Id -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Log "Error while cleaning frontend process: $($_.Exception.Message)" "Red"
    }
    
    # Cleanup backend process
    try {
        if ($global:BackendProcess -and !$global:BackendProcess.HasExited) {
            Write-Log "Stopping backend process (PID: $($global:BackendProcess.Id))..."
            $stopped = $false
            try {
                Stop-Process -Id $global:BackendProcess.Id -Force -ErrorAction Stop
                $stopped = $true
            } catch {
                Write-Log "Direct stop failed (likely due to elevation), trying elevated kill..." "Yellow"
            }

            if (-not $stopped) {
                try {
                    # Try elevated Stop-Process by PID
                    $elevCmd = "Stop-Process -Id $($global:BackendProcess.Id) -Force"
                    Start-Process -FilePath "powershell" -ArgumentList "-NoProfile","-WindowStyle","Hidden","-Command", $elevCmd -Verb RunAs -Wait | Out-Null
                } catch {
                    Write-Log "Elevated Stop-Process failed: $($_.Exception.Message)" "Red"
                }

                # If still running, try taskkill by PID then by image name
                try {
                    if ($global:BackendProcess -and !$global:BackendProcess.HasExited) {
                        Start-Process -FilePath "powershell" -ArgumentList "-NoProfile","-WindowStyle","Hidden","-Command", "taskkill /PID $($global:BackendProcess.Id) /F" -Verb RunAs -Wait | Out-Null
                    }
                } catch {
                    Write-Log "Elevated taskkill by PID failed: $($_.Exception.Message)" "Red"
                }

                try {
                    if ($global:BackendProcess -and !$global:BackendProcess.HasExited) {
                        Start-Process -FilePath "powershell" -ArgumentList "-NoProfile","-WindowStyle","Hidden","-Command", "taskkill /IM SystemMonitor.Service.exe /F" -Verb RunAs -Wait | Out-Null
                    }
                } catch {
                    Write-Log "Elevated taskkill by name failed: $($_.Exception.Message)" "Red"
                }
            }
        }
    } catch {
        Write-Log "Error while cleaning backend process: $($_.Exception.Message)" "Red"
    }
    
    # Deep cleanup via kill.ps1 if available
    try {
        $killScript = Join-Path $scriptDir "kill.ps1"
        if (Test-Path $killScript) {
            Write-Log "Running deep cleanup..."
            & $killScript -Verbose
        }
    } catch {
        Write-Log "Error while running kill.ps1: $($_.Exception.Message)" "Red"
    }
    
    Write-Log "Process cleanup completed" "Green"
}

# Register exit handler
$null = Register-EngineEvent PowerShell.Exiting -Action {
    Cleanup-AllProcesses
}

# Register Ctrl+C handler
try {
    [Console]::CancelKeyPress += {
        param($sender, $e)
        $e.Cancel = $true
        Write-Log "`nCtrl+C detected, cleaning up processes..." "Yellow"
        Cleanup-AllProcesses
        exit 0
    }
} catch {
    Write-Log "Note: Ctrl+C handling may not be fully supported; clean up manually if needed" "Yellow"
}

try {
    [Console]::TreatControlCAsInput = $false
} catch {
    # Ignore any errors
}

# If -NoKillExisting is not specified, clean existing related processes first
if (-not $NoKillExisting) {
    Write-Log "Cleaning existing related processes..." "Cyan"
    Cleanup-AllProcesses
}

try {
    Write-Log "=== Starting portable Sys Sensor ===" "Cyan"
    
    # Start backend service (elevated)
    $backendExe = Join-Path $scriptDir "backend\SystemMonitor.Service.exe"
    if (Test-Path $backendExe) {
        Write-Log "Starting backend service as administrator..." "Green"
        $global:BackendProcess = Start-Process -FilePath $backendExe -WindowStyle Normal -Verb RunAs -PassThru
        Write-Log "Backend started, PID: $($global:BackendProcess.Id)" "Green"
        
        # Wait for backend initialization
        Write-Log "Waiting for RPC pipe ready: \\.\pipe\sys_sensor_v3.rpc" "Cyan"
        function Wait-PipeReady { 
            param([int]$TimeoutSec = 15)
            $deadline = (Get-Date).AddSeconds($TimeoutSec)
            while ((Get-Date) -lt $deadline) { 
                try { 
                    $c = New-Object System.IO.Pipes.NamedPipeClientStream '.', 'sys_sensor_v3.rpc', ([System.IO.Pipes.PipeDirection]::InOut), ([System.IO.Pipes.PipeOptions]::Asynchronous)
                    $c.Connect(200)
                    $c.Dispose()
                    return $true 
                } catch { 
                    Start-Sleep -Milliseconds 200 
                } 
            } 
            return $false 
        }
        
        $pipeReady = Wait-PipeReady -TimeoutSec 15
        if ($pipeReady) { 
            Write-Log "RPC pipe is ready" "Green" 
        } else { 
            Write-Log "Warning: RPC pipe not ready, will continue to start frontend" "Yellow" 
        }
    } else {
        Write-Log "Error: backend executable not found: $backendExe" "Red"
        exit 1
    }
    
    # Start frontend app
    $frontendExe = Join-Path $scriptDir "frontend\sys-sensor-v3-app.exe"
    if (Test-Path $frontendExe) {
        Write-Log "Starting frontend app..." "Green"
        $global:FrontendProcess = Start-Process -FilePath $frontendExe -WindowStyle Normal -PassThru
        Write-Log "Frontend started, PID: $($global:FrontendProcess.Id)" "Green"
    } else {
        Write-Log "Error: frontend executable not found: $frontendExe" "Red"
        exit 1
    }
    
    Write-Log "Startup completed" "Green"
    Write-Log "Press Ctrl+C or close any window to exit" "Yellow"
    
    # Wait until any process exits
    while ($true) {
        # Backend
        if ($global:BackendProcess -and $global:BackendProcess.HasExited) {
            Write-Log "Backend exited, code: $($global:BackendProcess.ExitCode)" "Yellow"
            break
        }
        
        # Frontend
        if ($global:FrontendProcess -and $global:FrontendProcess.HasExited) {
            Write-Log "Frontend exited, code: $($global:FrontendProcess.ExitCode)" "Yellow"
            break
        }
        
        Start-Sleep -Milliseconds 500
    }
    
} catch {
    Write-Log "Error during startup: $($_.Exception.Message)" "Red"
    Write-Log "Stack trace: $($_.Exception.StackTrace)" "Red"
} finally {
    Write-Log "Shutting down..." "Yellow"
    Cleanup-AllProcesses
    Write-Log "Shutdown completed" "Green"
}