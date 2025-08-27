# 启动便携式版本的系统监控程序
# 该脚本会启动后端服务和前端应用，并在退出时清理所有进程

param(
    [switch]$NoKillExisting = $false
)

$ErrorActionPreference = "Continue"

function Write-Log {
    param([string]$Message, [string]$Color = "White")
    Write-Host "[portable-start] $Message" -ForegroundColor $Color
}

# 获取脚本所在目录
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Write-Log "脚本目录: $scriptDir" "Gray"

# 全局变量存储启动的进程
$global:BackendProcess = $null
$global:FrontendProcess = $null

# 清理函数
function Cleanup-AllProcesses {
    Write-Log "正在清理所有启动的进程..." "Yellow"
    
    # 清理前端进程
    try {
        if ($global:FrontendProcess -and !$global:FrontendProcess.HasExited) {
            Write-Log "停止前端进程 (PID: $($global:FrontendProcess.Id))..."
            Stop-Process -Id $global:FrontendProcess.Id -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Log "清理前端进程时出错: $($_.Exception.Message)" "Red"
    }
    
    # 清理后端进程
    try {
        if ($global:BackendProcess -and !$global:BackendProcess.HasExited) {
            Write-Log "停止后端进程 (PID: $($global:BackendProcess.Id))..."
            Stop-Process -Id $global:BackendProcess.Id -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Log "清理后端进程时出错: $($_.Exception.Message)" "Red"
    }
    
    # 使用kill.ps1进行彻底清理
    try {
        $killScript = Join-Path $scriptDir "kill.ps1"
        if (Test-Path $killScript) {
            Write-Log "执行彻底清理..."
            & $killScript -Verbose
        }
    } catch {
        Write-Log "执行kill.ps1时出错: $($_.Exception.Message)" "Red"
    }
    
    Write-Log "进程清理完成" "Green"
}

# 注册退出处理程序
$null = Register-EngineEvent PowerShell.Exiting -Action {
    Cleanup-AllProcesses
}

# 注册Ctrl+C处理程序
try {
    [Console]::CancelKeyPress += {
        param($sender, $e)
        $e.Cancel = $true
        Write-Log "`n检测到Ctrl+C，正在清理进程..." "Yellow"
        Cleanup-AllProcesses
        exit 0
    }
} catch {
    Write-Log "注意: Ctrl+C处理可能不完全支持，请手动清理进程" "Yellow"
}

try {
    [Console]::TreatControlCAsInput = $false
} catch {
    # 忽略设置错误
}

# 如果没有指定-NoKillExisting参数，则先清理已存在的进程
if (-not $NoKillExisting) {
    Write-Log "清理已存在的相关进程..." "Cyan"
    Cleanup-AllProcesses
}

try {
    Write-Log "=== 启动便携式系统监控程序 ===" "Cyan"
    
    # 启动后端服务
    $backendExe = Join-Path $scriptDir "backend\SystemMonitor.Service.exe"
    if (Test-Path $backendExe) {
        Write-Log "启动后端服务..." "Green"
        $global:BackendProcess = Start-Process -FilePath $backendExe -WindowStyle Normal -PassThru
        Write-Log "后端服务已启动，PID: $($global:BackendProcess.Id)" "Green"
        
        # 等待后端服务初始化完成
        Write-Log "等待RPC管道准备就绪: \\.\pipe\sys_sensor_v3.rpc" "Cyan"
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
            Write-Log "RPC管道已就绪" "Green" 
        } else { 
            Write-Log "警告: RPC管道未就绪，将继续启动前端" "Yellow" 
        }
    } else {
        Write-Log "错误: 未找到后端可执行文件 $backendExe" "Red"
        exit 1
    }
    
    # 启动前端应用
    $frontendExe = Join-Path $scriptDir "frontend\sys-sensor-v3-app.exe"
    if (Test-Path $frontendExe) {
        Write-Log "启动前端应用..." "Green"
        $global:FrontendProcess = Start-Process -FilePath $frontendExe -WindowStyle Normal -PassThru
        Write-Log "前端应用已启动，PID: $($global:FrontendProcess.Id)" "Green"
    } else {
        Write-Log "错误: 未找到前端可执行文件 $frontendExe" "Red"
        exit 1
    }
    
    Write-Log "程序启动完成！" "Green"
    Write-Log "按Ctrl+C或关闭任一窗口可退出程序" "Yellow"
    
    # 等待任一进程退出
    while ($true) {
        # 检查后端进程
        if ($global:BackendProcess -and $global:BackendProcess.HasExited) {
            Write-Log "后端服务已退出，退出代码: $($global:BackendProcess.ExitCode)" "Yellow"
            break
        }
        
        # 检查前端进程
        if ($global:FrontendProcess -and $global:FrontendProcess.HasExited) {
            Write-Log "前端应用已退出，退出代码: $($global:FrontendProcess.ExitCode)" "Yellow"
            break
        }
        
        Start-Sleep -Milliseconds 500
    }
    
} catch {
    Write-Log "启动过程中发生错误: $($_.Exception.Message)" "Red"
    Write-Log "堆栈跟踪: $($_.Exception.StackTrace)" "Red"
} finally {
    Write-Log "正在关闭程序..." "Yellow"
    Cleanup-AllProcesses
    Write-Log "程序已关闭" "Green"
}