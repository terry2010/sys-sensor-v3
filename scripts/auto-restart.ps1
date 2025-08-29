# SystemMonitor Service 自动重启监控脚本
# 用于检测服务异常并自动重启，防止长时间运行导致堆栈溢出

param(
    [string]$ServiceExe = "SystemMonitor.Service.exe",
    [string]$LogFile = "auto-restart.log",
    [int]$MaxMemoryMB = 200,    # 内存超过200MB时强制重启
    [int]$MaxHandles = 1000,    # 句柄数超过1000时强制重启
    [int]$MaxThreads = 35,      # 线程数超过35时强制重启
    [int]$MaxUptime = 1440,     # 最大运行时间（分钟），默认24小时
    [int]$CheckInterval = 300,  # 检查间隔（秒）
    [switch]$Force               # 强制模式，不检查进程状态直接重启
)

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $logEntry = "[$timestamp] $Message"
    Write-Host $logEntry
    Add-Content -Path $LogFile -Value $logEntry -Encoding UTF8
}

Write-Log "=== SystemMonitor Service 自动重启监控开始 ==="
Write-Log "监控目标: $ServiceExe"
Write-Log "检查间隔: $CheckInterval 秒"
Write-Log "最大内存: $MaxMemoryMB MB"
Write-Log "最大句柄数: $MaxHandles"
Write-Log "最大线程数: $MaxThreads"
Write-Log "最大运行时间: $MaxUptime 分钟"
Write-Log "日志文件: $LogFile"

function Restart-Service {
    param([string]$Reason)
    Write-Log "准备重启服务，原因: $Reason"
    
    # 尝试优雅关闭
    try {
        $processes = Get-Process -Name $ServiceExe.Replace(".exe", "") -ErrorAction SilentlyContinue
        if ($processes) {
            Write-Log "正在优雅关闭服务..."
            foreach ($process in $processes) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
            Start-Sleep -Seconds 5
        }
    } catch {
        Write-Log "优雅关闭失败: $($_.Exception.Message)"
    }
    
    # 确保进程已经终止
    try {
        $stillRunning = Get-Process -Name $ServiceExe.Replace(".exe", "") -ErrorAction SilentlyContinue
        if ($stillRunning) {
            Write-Log "服务仍在运行，强制终止..."
            foreach ($process in $stillRunning) {
                taskkill /F /PID $process.Id 2>$null
            }
            Start-Sleep -Seconds 2
        }
    } catch {
        Write-Log "强制终止失败: $($_.Exception.Message)"
    }
    
    # 启动服务
    try {
        Write-Log "正在启动服务..."
        $serviceExePath = Join-Path (Get-Location) "src\SystemMonitor.Service\bin\Debug\net8.0\$ServiceExe"
        if (Test-Path $serviceExePath) {
            Start-Process -FilePath $serviceExePath -WindowStyle Hidden
            Write-Log "服务已启动"
        } else {
            Write-Log "找不到服务可执行文件: $serviceExePath"
            Write-Log "尝试使用dotnet run启动服务..."
            Start-Process -FilePath "dotnet" -ArgumentList "run --project src\SystemMonitor.Service" -WindowStyle Hidden
            Write-Log "服务已通过dotnet run启动"
        }
    } catch {
        Write-Log "启动服务失败: $($_.Exception.Message)"
    }
}

if ($Force) {
    Write-Log "强制模式：不检查进程状态，直接重启服务"
    Restart-Service "强制重启请求"
    Write-Log "强制重启完成"
    exit 0
}

$lastRestartTime = Get-Date
$checkCount = 0

while ($true) {
    $checkCount++
    
    Write-Log "[$checkCount] $(Get-Date -Format 'HH:mm:ss') 检查服务状态..."
    
    # 检查是否需要定期重启（预防性维护）
    $currentTime = Get-Date
    $timeSinceLastRestart = ($currentTime - $lastRestartTime).TotalMinutes
    
    if ($timeSinceLastRestart -ge $MaxUptime) {
        Write-Log "服务已运行 $([int]$timeSinceLastRestart) 分钟，超过最大运行时间 $MaxUptime 分钟，执行预防性重启"
        Restart-Service "定期预防性维护"
        $lastRestartTime = Get-Date
        Start-Sleep -Seconds $CheckInterval
        continue
    }
    
    # 检查服务进程是否存在
    $serviceProc = Get-Process -Name $ServiceExe.Replace(".exe", "") -ErrorAction SilentlyContinue
    
    if (-not $serviceProc) {
        Write-Log "服务进程未运行，正在启动..."
        Restart-Service "进程不存在"
        $lastRestartTime = Get-Date
    } else {
        # 检查资源使用情况
        $memoryMB = [math]::Round($serviceProc.WorkingSet64/1MB, 1)
        $handles = $serviceProc.HandleCount
        $threads = $serviceProc.Threads.Count
        $uptime = (Get-Date) - $serviceProc.StartTime
        
        Write-Log "  内存使用: ${memoryMB}MB | 句柄数: $handles | 线程数: $threads | 运行时间: $([int]$uptime.TotalMinutes)分钟"
        
        $needRestart = $false
        $restartReason = ""
        
        # 检查内存使用
        if ($memoryMB -gt $MaxMemoryMB) {
            $needRestart = $true
            $restartReason += "内存使用过高(${memoryMB}MB > ${MaxMemoryMB}MB) "
        }
        
        # 检查句柄数
        if ($handles -gt $MaxHandles) {
            $needRestart = $true
            $restartReason += "句柄数过多($handles > $MaxHandles) "
        }
        
        # 检查线程数
        if ($threads -gt $MaxThreads) {
            $needRestart = $true
            $restartReason += "线程数过多($threads > $MaxThreads) "
        }
        
        if ($needRestart) {
            Write-Log "检测到资源异常: $restartReason"
            Restart-Service $restartReason
            $lastRestartTime = Get-Date
        } else {
            Write-Log "服务运行正常"
        }
    }
    
    Start-Sleep -Seconds $CheckInterval
}