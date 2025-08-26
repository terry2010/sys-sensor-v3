# SystemMonitor 服务进程监控脚本
# 用于检测服务进程异常退出并记录详细信息

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

Write-Log "=== SystemMonitor 服务监控开始 ==="
Write-Log "监控目标: $ServiceExe"
Write-Log "检查间隔: $CheckIntervalSeconds 秒"
Write-Log "日志文件: $LogFile"

$lastProcessId = $null
$processStartTime = $null
$checkCount = 0

while ($true) {
    $checkCount++
    
    try {
        # 查找 SystemMonitor 服务进程
        $processes = Get-Process -Name "SystemMonitor.Service" -ErrorAction SilentlyContinue
        
        if ($processes) {
            $process = $processes[0]  # 取第一个进程
            $currentProcessId = $process.Id
            
            # 检查是否是新进程
            if ($lastProcessId -ne $currentProcessId) {
                if ($lastProcessId) {
                    Write-Log "检测到进程变化: 旧PID=$lastProcessId, 新PID=$currentProcessId"
                    $crashDuration = (Get-Date) - $processStartTime
                    Write-Log "前一个进程运行时长: $($crashDuration.ToString())"
                    
                    # 查看Windows事件日志中的相关错误
                    try {
                        $recentErrors = Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2; StartTime=(Get-Date).AddMinutes(-5)} -MaxEvents 10 -ErrorAction SilentlyContinue |
                            Where-Object { $_.ProviderName -like "*SystemMonitor*" -or $_.Message -like "*SystemMonitor*" }
                        
                        if ($recentErrors) {
                            Write-Log "发现相关错误日志:"
                            foreach ($error in $recentErrors) {
                                Write-Log "  [事件日志] $($error.TimeCreated): $($error.LevelDisplayName) - $($error.Message)"
                            }
                        }
                    } catch {
                        Write-Log "无法读取事件日志: $($_.Exception.Message)"
                    }
                }
                
                $lastProcessId = $currentProcessId
                $processStartTime = $process.StartTime
                Write-Log "监控新进程: PID=$currentProcessId, 启动时间=$($process.StartTime), 内存=$([math]::Round($process.WorkingSet64/1MB,2))MB"
            }
            
            # 记录进程健康状态（每分钟记录一次）
            if ($checkCount % (60 / $CheckIntervalSeconds) -eq 0) {
                $memoryMB = [math]::Round($process.WorkingSet64 / 1MB, 2)
                $handleCount = $process.HandleCount
                $threadCount = $process.Threads.Count
                $runTime = (Get-Date) - $process.StartTime
                
                Write-Log "进程健康检查: PID=$($process.Id), 运行时长=$($runTime.ToString()), 内存=${memoryMB}MB, 句柄=$handleCount, 线程=$threadCount"
                
                # 检查资源使用异常
                if ($memoryMB -gt 500) {
                    Write-Log "[警告] 内存使用量较高: ${memoryMB}MB"
                }
                if ($handleCount -gt 1000) {
                    Write-Log "[警告] 句柄数量较多: $handleCount"
                }
            }
            
        } else {
            if ($lastProcessId) {
                Write-Log "[严重] 进程已消失! 上一个PID=$lastProcessId"
                $crashDuration = (Get-Date) - $processStartTime
                Write-Log "进程运行时长: $($crashDuration.ToString())"
                
                # 尝试查找崩溃原因
                Write-Log "正在查找崩溃原因..."
                
                # 检查最近的系统错误
                try {
                    $systemErrors = Get-WinEvent -FilterHashtable @{LogName='System'; Level=1,2,3; StartTime=(Get-Date).AddMinutes(-2)} -MaxEvents 5 -ErrorAction SilentlyContinue
                    if ($systemErrors) {
                        Write-Log "发现系统错误:"
                        foreach ($error in $systemErrors) {
                            Write-Log "  [系统] $($error.TimeCreated): $($error.LevelDisplayName) - $($error.Message)"
                        }
                    }
                } catch {}
                
                # 检查应用程序错误
                try {
                    $appErrors = Get-WinEvent -FilterHashtable @{LogName='Application'; Level=1,2; StartTime=(Get-Date).AddMinutes(-2)} -MaxEvents 10 -ErrorAction SilentlyContinue
                    if ($appErrors) {
                        Write-Log "发现应用程序错误:"
                        foreach ($error in $appErrors) {
                            Write-Log "  [应用] $($error.TimeCreated): $($error.LevelDisplayName) - $($error.Message)"
                        }
                    }
                } catch {}
                
                $lastProcessId = $null
                $processStartTime = $null
            } else {
                Write-Log "SystemMonitor.Service 进程未找到 (检查 #$checkCount)"
            }
        }
        
    } catch {
        Write-Log "[错误] 监控异常: $($_.Exception.Message)"
    }
    
    Start-Sleep -Seconds $CheckIntervalSeconds
}