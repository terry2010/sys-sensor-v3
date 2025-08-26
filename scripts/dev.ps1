<#
  Development Environment Startup Script
  Flow: Build(retry) -> Start Backend -> Wait for Named Pipe -> Start Frontend -> Wait -> Cleanup
  Features:
  - Does not kill existing processes on startup
  - Ctrl+C will cleanup all processes started by this session
  - Integrates with kill.ps1 for thorough cleanup
#>

$ErrorActionPreference = 'Continue'

# Set UTF-8 encoding for console and PowerShell
chcp 65001 1>$null 2>$null
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = (Resolve-Path (Join-Path $PSScriptRoot '..'))
Set-Location $root

$ServiceProject = 'src/SystemMonitor.Service/SystemMonitor.Service.csproj'
$FrontendDir = 'frontend'
$ServiceExePath = Join-Path $root 'src\SystemMonitor.Service\bin\Debug\net8.0\SystemMonitor.Service.exe'

# Global variables to store started processes for Ctrl+C cleanup
$global:BackendProcess = $null
$global:FrontendProcess = $null

# Define cleanup function
function Cleanup-AllProcesses {
    Write-Host '[dev] Cleaning up all started processes...' -ForegroundColor Yellow
    
    # Cleanup frontend process
    try {
        if ($global:FrontendProcess -and !$global:FrontendProcess.HasExited) {
            Write-Host '[dev] Stopping frontend process...'
            taskkill /PID $global:FrontendProcess.Id /T /F | Out-Null
        }
    } catch {
        Write-Host '[dev] Error cleaning frontend process:' $_.Exception.Message -ForegroundColor Red
    }
    
    # Cleanup backend process
    try {
        if ($global:BackendProcess -and !$global:BackendProcess.HasExited) {
            Write-Host '[dev] Stopping backend process...'
            taskkill /PID $global:BackendProcess.Id /T /F | Out-Null
        }
    } catch {
        Write-Host '[dev] Error cleaning backend process:' $_.Exception.Message -ForegroundColor Red
    }
    
    # Call kill.ps1 for thorough cleanup
    try {
        Write-Host '[dev] Executing thorough cleanup...'
        & "$PSScriptRoot\kill.ps1"
    } catch {
        Write-Host '[dev] Error executing kill.ps1:' $_.Exception.Message -ForegroundColor Red
        # Backup cleanup plan
        Get-Process rustc,cargo,tauri,node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        try { taskkill /F /T /IM SystemMonitor.Service.exe | Out-Null } catch { }
    }
    
    Write-Host '[dev] Process cleanup completed' -ForegroundColor Green
}

# Register Ctrl+C and exit handlers
$null = Register-EngineEvent PowerShell.Exiting -Action {
    Cleanup-AllProcesses
}

# Register Ctrl+C handler with better compatibility
try {
    # Try modern approach
    [Console]::CancelKeyPress += {
        param($sender, $e)
        $e.Cancel = $true
        Write-Host "`n[dev] Detected Ctrl+C, cleaning up processes..." -ForegroundColor Yellow
        Cleanup-AllProcesses
        exit 0
    }
} catch {
    # If failed, use traditional approach
    Write-Host '[dev] Note: Ctrl+C handling may not be fully supported, please cleanup manually when exiting' -ForegroundColor Yellow
}

try {
    # Set console to handle Ctrl+C
    [Console]::TreatControlCAsInput = $false
} catch {
    # Ignore setting errors
}

Write-Host '[dev] Starting backend service build (max 3 retries)'
$ok = $false
for ($i=1; $i -le 3; $i++) {
  Write-Host ("[dev] Build attempt {0}/3" -f $i)
  & dotnet build $ServiceProject -c Debug -v minimal --nologo
  if ($LASTEXITCODE -eq 0) { 
    $ok = $true
    Write-Host '[dev] Build successful' -ForegroundColor Green
    break 
  }
  Write-Host '[dev] Build failed, retrying in 2 seconds...' -ForegroundColor Yellow
  Start-Sleep -Seconds 2
}
if (-not $ok) { 
  Write-Host '[dev] Build failed, please check code errors' -ForegroundColor Red
  exit 1 
}

Write-Host '[dev] Starting backend service...' -ForegroundColor Cyan
$global:BackendProcess = Start-Process -FilePath 'dotnet' -ArgumentList @('run','-c','Debug','--project',$ServiceProject) -PassThru -WindowStyle Hidden
Write-Host "[dev] Backend process started, PID: $($global:BackendProcess.Id)" -ForegroundColor Green

Write-Host '[dev] Waiting for RPC pipe ready: \\.\pipe\sys_sensor_v3.rpc' -ForegroundColor Cyan
function _WaitPipe { param([int]$TimeoutSec = 15); $deadline=(Get-Date).AddSeconds($TimeoutSec); while ((Get-Date) -lt $deadline) { try { $c = New-Object System.IO.Pipes.NamedPipeClientStream '.', 'sys_sensor_v3.rpc', ([System.IO.Pipes.PipeDirection]::InOut), ([System.IO.Pipes.PipeOptions]::Asynchronous); $c.Connect(200); $c.Dispose(); return $true } catch { Start-Sleep -Milliseconds 200 } } return $false }
$ready = _WaitPipe -TimeoutSec 15
if ($ready) { 
  Write-Host '[dev] RPC pipe is ready' -ForegroundColor Green 
} else { 
  Write-Host '[dev] Warning: RPC pipe not ready, but will continue to start frontend' -ForegroundColor Yellow 
}

Write-Host '[dev] Starting frontend application...' -ForegroundColor Cyan
Push-Location $FrontendDir
if (-not (Test-Path 'node_modules')) { 
    Write-Host '[dev] Installing frontend dependencies...' -ForegroundColor Yellow
    npm install 
}
$global:FrontendProcess = Start-Process -FilePath 'npm' -ArgumentList @('run','tauri:dev') -PassThru
Write-Host "[dev] Frontend process started, PID: $($global:FrontendProcess.Id)" -ForegroundColor Green
Pop-Location

Write-Host '[dev] Development environment started' -ForegroundColor Green
Write-Host '[dev] Press Ctrl+C to cleanup all processes and exit' -ForegroundColor Yellow
Write-Host '[dev] Waiting for frontend application to exit...' -ForegroundColor Cyan

try {
    if ($global:FrontendProcess) { 
        Wait-Process -Id $global:FrontendProcess.Id 
    } else { 
        # If frontend process failed to start, wait for user manual exit
        Write-Host '[dev] Frontend process did not start normally, press any key to exit...'
        $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    }
} catch {
    Write-Host '[dev] Exception occurred while waiting for process:' $_.Exception.Message
} finally {
    Cleanup-AllProcesses
}

Write-Host '[dev] Development environment closed' -ForegroundColor Green