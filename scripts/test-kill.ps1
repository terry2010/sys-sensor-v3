# 测试 kill.ps1 脚本功能

Write-Host "测试 kill.ps1 脚本..." -ForegroundColor Cyan

# 首先检查是否有相关进程在运行
Write-Host "`n检查当前运行的相关进程:" -ForegroundColor Yellow

$processes = @()
$keywords = @("SystemMonitor", "dotnet", "node", "tauri")

foreach ($keyword in $keywords) {
    $procs = Get-Process | Where-Object { $_.ProcessName -like "*$keyword*" } -ErrorAction SilentlyContinue
    if ($procs) {
        $processes += $procs
        Write-Host "发现 $keyword 相关进程: $($procs.Count) 个" -ForegroundColor White
        foreach ($proc in $procs) {
            Write-Host "  - PID: $($proc.Id), 名称: $($proc.ProcessName)" -ForegroundColor Gray
        }
    }
}

if ($processes.Count -eq 0) {
    Write-Host "当前没有发现相关进程在运行" -ForegroundColor Green
} else {
    Write-Host "`n总共发现 $($processes.Count) 个相关进程" -ForegroundColor Yellow
}

Write-Host "`n现在执行 kill.ps1 进行清理..." -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan

# 执行 kill.ps1
& "$PSScriptRoot\kill.ps1" -Verbose

Write-Host "`n================================" -ForegroundColor Cyan
Write-Host "kill.ps1 执行完成" -ForegroundColor Green

# 再次检查进程
Write-Host "`n检查清理后的进程状态:" -ForegroundColor Yellow
$remainingProcesses = @()

foreach ($keyword in $keywords) {
    $procs = Get-Process | Where-Object { $_.ProcessName -like "*$keyword*" } -ErrorAction SilentlyContinue
    if ($procs) {
        $remainingProcesses += $procs
        Write-Host "仍存在 $keyword 相关进程: $($procs.Count) 个" -ForegroundColor Red
        foreach ($proc in $procs) {
            Write-Host "  - PID: $($proc.Id), 名称: $($proc.ProcessName)" -ForegroundColor Gray
        }
    }
}

if ($remainingProcesses.Count -eq 0) {
    Write-Host "所有相关进程已被清理" -ForegroundColor Green
} else {
    Write-Host "仍有 $($remainingProcesses.Count) 个进程未被清理" -ForegroundColor Yellow
}

Write-Host "`nkill.ps1 测试完成" -ForegroundColor Cyan