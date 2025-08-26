# 安全测试脚本 - 预览kill.ps1会影响哪些进程

Write-Host "[test-kill] 检查当前运行的相关进程..." -ForegroundColor Cyan

# 获取当前进程ID
$currentPid = $PID
Write-Host "[test-kill] 当前脚本进程ID: $currentPid" -ForegroundColor Gray

# 检查SystemMonitor相关进程
Write-Host "`n[test-kill] 检查SystemMonitor相关进程:" -ForegroundColor Yellow
$systemMonitorProcs = Get-Process | Where-Object { 
    $_.ProcessName -like "*SystemMonitor*"
}

if ($systemMonitorProcs) {
    foreach ($proc in $systemMonitorProcs) {
        $willKill = $true
        
        # 安全检查
        if ($proc.Id -eq $currentPid) { 
            $willKill = $false
            $reason = "(当前脚本进程)" 
        } elseif ($proc.ProcessName -like '*Code*' -or $proc.ProcessName -like '*Qoder*') { 
            $willKill = $false
            $reason = "(编辑器进程)" 
        }
        
        if ($willKill) {
            Write-Host "  PID: $($proc.Id), 名称: $($proc.ProcessName), 状态: 会被杀死" -ForegroundColor Red
        } else {
            Write-Host "  PID: $($proc.Id), 名称: $($proc.ProcessName), 状态: 受保护 $reason" -ForegroundColor Green
        }
    }
} else {
    Write-Host "  未发现SystemMonitor相关进程" -ForegroundColor Gray
}

# 检查dotnet进程
Write-Host "`n[test-kill] 检查dotnet相关进程:" -ForegroundColor Yellow
$dotnetProcs = Get-Process dotnet -ErrorAction SilentlyContinue
if ($dotnetProcs) {
    foreach ($proc in $dotnetProcs) {
        Write-Host "  PID: $($proc.Id), 名称: $($proc.ProcessName), 状态: 可能被杀死" -ForegroundColor Yellow
    }
} else {
    Write-Host "  未发现dotnet进程" -ForegroundColor Gray
}

# 检查Tauri进程
Write-Host "`n[test-kill] 检查Tauri相关进程:" -ForegroundColor Yellow
$tauriProcs = Get-Process | Where-Object { 
    $_.ProcessName -like "*sys-sensor-v3*" -or 
    $_.ProcessName -like "*tauri*"
}

if ($tauriProcs) {
    foreach ($proc in $tauriProcs) {
        Write-Host "  PID: $($proc.Id), 名称: $($proc.ProcessName), 状态: 会被杀死" -ForegroundColor Red
    }
} else {
    Write-Host "  未发现Tauri相关进程" -ForegroundColor Gray
}

Write-Host "`n[test-kill] 检查完成。如果要实际执行清理，请运行 kill.ps1" -ForegroundColor Green