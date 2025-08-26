# 安全测试脚本 - 预览kill.ps1会影响哪些进程
# 只显示信息，不实际杀死进程

param(
    [switch]$Verbose = $false
)

function Write-Log {
    param([string]$Message, [string]$Color = "White")
    Write-Host "[test-kill] $Message" -ForegroundColor $Color
}

Write-Log "开始检查当前运行的相关进程..." "Cyan"

# 获取当前进程ID和父进程ID，避免自杀
$currentPid = $PID
$parentPid = (Get-WmiObject Win32_Process -Filter "ProcessId=$currentPid" -ErrorAction SilentlyContinue).ParentProcessId

Write-Log "当前脚本进程ID: $currentPid" "Gray"
Write-Log "父进程ID: $parentPid" "Gray"

# 检查SystemMonitor相关进程
Write-Log "`n检查SystemMonitor相关进程:" "Yellow"
$systemMonitorProcs = Get-Process | Where-Object { 
    $_.ProcessName -like "*SystemMonitor*" -or 
    $_.MainWindowTitle -like "*SystemMonitor*"
}

if ($systemMonitorProcs) {
    foreach ($proc in $systemMonitorProcs) {
        $willKill = $true
        $reason = ""
        
        # 安全检查
        if ($proc.Id -eq $currentPid) { $willKill = $false; $reason = "(当前脚本进程)" }
        elseif ($parentPid -and $proc.Id -eq $parentPid) { $willKill = $false; $reason = "(父进程)" }
        elseif ($proc.ProcessName -in @('explorer', 'winlogon', 'csrss', 'services', 'lsass', 'svchost')) { $willKill = $false; $reason = "(系统关键进程)" }
        elseif ($proc.ProcessName -like '*Code*' -or $proc.ProcessName -like '*devenv*' -or $proc.ProcessName -like '*Qoder*') { $willKill = $false; $reason = "(编辑器进程)" }
        
        $status = if ($willKill) { "会被杀死" } else { "受保护 $reason" }
        $color = if ($willKill) { "Red" } else { "Green" }
        
        Write-Log "  PID: $($proc.Id), 名称: $($proc.ProcessName), 状态: $status" $color
    }
} else {
    Write-Log "  未发现SystemMonitor相关进程" "Gray"
}

# 检查dotnet进程
Write-Log "`n检查dotnet相关进程:" "Yellow"
try {
    $dotnetProcs = Get-Process dotnet -ErrorAction SilentlyContinue | Where-Object {
        $_.CommandLine -like "*SystemMonitor*" -or 
        $_.StartInfo.Arguments -like "*SystemMonitor*"
    }
    
    if ($dotnetProcs) {
        foreach ($proc in $dotnetProcs) {
            Write-Log "  PID: $($proc.Id), 参数: $($proc.StartInfo.Arguments), 状态: 会被杀死" "Red"
        }
    } else {
        Write-Log "  未发现相关dotnet进程" "Gray"
    }
} catch {
    Write-Log "  检查dotnet进程时出现问题: $($_.Exception.Message)" "Gray"
}

# 检查Tauri进程
Write-Log "`n检查Tauri相关进程:" "Yellow"
$tauriProcs = Get-Process | Where-Object { 
    $_.ProcessName -like "*sys-sensor-v3*" -or 
    $_.ProcessName -like "*tauri*"
}

if ($tauriProcs) {
    foreach ($proc in $tauriProcs) {
        Write-Log "  PID: $($proc.Id), 名称: $($proc.ProcessName), 状态: 会被杀死" "Red"
    }
} else {
    Write-Log "  未发现Tauri相关进程" "Gray"
}

# 检查Node.js进程
Write-Log "`n检查Node.js相关进程:" "Yellow"
try {
    $nodeProcs = Get-Process node -ErrorAction SilentlyContinue | Where-Object {
        $_.CommandLine -like "*sys-sensor*" -or 
        $_.CommandLine -like "*vite*" -or
        $_.MainWindowTitle -like "*sys-sensor*"
    }
    
    if ($nodeProcs) {
        foreach ($proc in $nodeProcs) {
            Write-Log "  PID: $($proc.Id), 名称: $($proc.ProcessName), 状态: 会被杀死" "Red"
        }
    } else {
        Write-Log "  未发现相关Node.js进程" "Gray"
    }
} catch {
    Write-Log "  检查Node.js进程时出现问题: $($_.Exception.Message)" "Gray"
}

Write-Log "`n检查完成。如果要实际执行清理，请运行 kill.ps1" "Green"