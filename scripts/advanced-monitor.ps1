# SystemMonitor Service 高级监控与自动恢复脚本
# 检测服务异常、内存泄漏、堆栈溢出并自动重启服务

param(
    [string]$ServiceExe = "SystemMonitor.Service.exe",
    [string]$LogFile = "service-monitor-advanced.log",
    [int]$CheckIntervalSeconds = 10,
    [int]$MaxMemoryMB = 180,       # 内存阈值，超过则重启
    [int]$MaxHandles = 950,        # 句柄阈值，超过则重启
    [int]$MaxThreads = 40,         # 线程阈值，超过则重启
    [int]$MaxUptimeMinutes = 720,  # 最大运行时间（12小时），超过则预防性重启
    [switch]$EnableFrontendCheck = $true  # 是否监控前端连接
)

function Write-Log {
    param([string]$Message, [string]$Type = "INFO")
    
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
    $logEntry = "[$timestamp] [$Type] $Message"
    
    # 输出到控制台并根据类型设置颜色
    switch ($Type) {
        "ERROR" { Write-Host $logEntry -ForegroundColor Red }
        "WARNING" { Write-Host $logEntry -ForegroundColor Yellow }
        "SUCCESS" { Write-Host $logEntry -ForegroundColor Green }
        default { Write-Host $logEntry }
    }
    
    # 写入日志文件
    Add-Content -Path $LogFile -Value $logEntry -Encoding UTF8
}

function Start-ServiceProcess {
    param([string]$Reason = "")
    
    if ($Reason) {
        Write-Log "准备启动服务: $Reason" "INFO"
    } else {
        Write-Log "准备启动服务" "INFO"
    }
    
    # 尝试优雅地关闭已存在的服务进程
    $existingProc = Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($ServiceExe)) -ErrorAction SilentlyContinue
    if ($existingProc) {
        Write-Log "发现已存在的服务进程，正在尝试关闭..." "WARNING"
        try {
            $existingProc | Stop-Process -Force
            Start-Sleep -Seconds 2
        } catch {
            Write-Log "关闭现有服务进程时出错: $($_.Exception.Message)" "ERROR"
        }
    }
    
    # 查找可执行文件
    $serviceExePath = Join-Path -Path (Get-Location) -ChildPath "src\SystemMonitor.Service\bin\Debug\net8.0\$ServiceExe"
    $serviceDll = Join-Path -Path (Get-Location) -ChildPath "src\SystemMonitor.Service\bin\Debug\net8.0\SystemMonitor.Service.dll"
    
    try {
        if (Test-Path $serviceExePath) {
            Write-Log "使用EXE启动服务: $serviceExePath" "INFO"
            Start-Process -FilePath $serviceExePath -WindowStyle Hidden
            $result = $true
        } elseif (Test-Path $serviceDll) {
            Write-Log "使用dotnet命令启动服务: $serviceDll" "INFO"
            Start-Process -FilePath "dotnet" -ArgumentList "run --project src\SystemMonitor.Service" -WindowStyle Hidden
            $result = $true
        } else {
            Write-Log "无法找到服务可执行文件，尝试从源码构建并运行" "WARNING"
            Start-Process -FilePath "dotnet" -ArgumentList "build src\SystemMonitor.Service -c Debug" -Wait -WindowStyle Hidden
            Start-Process -FilePath "dotnet" -ArgumentList "run --project src\SystemMonitor.Service" -WindowStyle Hidden
            $result = $true
        }
        
        Write-Log "服务启动命令已执行" "SUCCESS"
        
        # 等待服务启动
        $startTime = Get-Date
        $maxWaitTime = [TimeSpan]::FromSeconds(30)
        $isRunning = $false
        
        while (((Get-Date) - $startTime) -lt $maxWaitTime) {
            Start-Sleep -Seconds 1
            
            $proc = Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($ServiceExe)) -ErrorAction SilentlyContinue
            if ($proc) {
                $isRunning = $true
                Write-Log "服务已成功启动，PID: $($proc.Id)" "SUCCESS"
                break
            }
        }
        
        if (-not $isRunning) {
            Write-Log "服务未在预期时间内启动，可能存在问题" "WARNING"
        }
        
        return $result
    } catch {
        Write-Log "启动服务时出错: $($_.Exception.Message)" "ERROR"
        return $false
    }
}

function Test-NamedPipe {
    param([string]$PipeName = "sys_sensor_v3.rpc")
    
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
        $pipe.Connect(100)  # 尝试连接，超时100ms
        $pipe.Dispose()
        return $true
    } catch {
        return $false
    }
}

function Get-ServiceDiagnostics {
    param([System.Diagnostics.Process]$Process)
    
    if (-not $Process) { return $null }
    
    # 获取进程诊断信息
    $diagnostics = @{
        PID = $Process.Id
        ProcessName = $Process.ProcessName
        StartTime = $Process.StartTime
        Uptime = (Get-Date) - $Process.StartTime
        MemoryMB = [math]::Round($Process.WorkingSet64 / 1MB, 1)
        PrivateMemoryMB = [math]::Round($Process.PrivateMemorySize64 / 1MB, 1)
        Handles = $Process.HandleCount
        Threads = $Process.Threads.Count
        CPU = $Process.CPU
        BasePriority = $Process.BasePriority
        PriorityClass = $Process.PriorityClass
        PipeAvailable = Test-NamedPipe -PipeName "sys_sensor_v3.rpc"
    }
    
    # 判断健康状态
    $diagnostics.MemoryWarning = $diagnostics.MemoryMB -gt $MaxMemoryMB
    $diagnostics.HandlesWarning = $diagnostics.Handles -gt $MaxHandles
    $diagnostics.ThreadsWarning = $diagnostics.Threads -gt $MaxThreads
    $diagnostics.UptimeWarning = $diagnostics.Uptime.TotalMinutes -gt $MaxUptimeMinutes
    
    # 综合判断健康状态
    $diagnostics.IsHealthy = -not (
        $diagnostics.MemoryWarning -or
        $diagnostics.HandlesWarning -or
        $diagnostics.ThreadsWarning -or
        $diagnostics.UptimeWarning -or
        (-not $diagnostics.PipeAvailable)
    )
    
    return $diagnostics
}

function Get-EventLogCrashes {
    param([int]$HoursBack = 1)
    
    try {
        $events = Get-WinEvent -FilterHashtable @{
            LogName = 'Application'
            Id = 1000  # 应用程序崩溃事件ID
            StartTime = (Get-Date).AddHours(-$HoursBack)
        } -MaxEvents 10 -ErrorAction SilentlyContinue | 
        Where-Object { $_.Message -like "*$ServiceExe*" -or $_.Message -like "*SystemMonitor.Service*" }
        
        return $events
    } catch {
        Write-Log "获取事件日志时出错: $($_.Exception.Message)" "WARNING"
        return @()
    }
}

function Get-HealthSummary {
    param($Diagnostics)
    
    if (-not $Diagnostics) {
        return "服务未运行"
    }
    
    $summary = "健康状态: "
    if ($Diagnostics.IsHealthy) {
        $summary += "正常"
    } else {
        $issues = @()
        if ($Diagnostics.MemoryWarning) { $issues += "内存过高($($Diagnostics.MemoryMB)MB)" }
        if ($Diagnostics.HandlesWarning) { $issues += "句柄过多($($Diagnostics.Handles))" }
        if ($Diagnostics.ThreadsWarning) { $issues += "线程过多($($Diagnostics.Threads))" }
        if ($Diagnostics.UptimeWarning) { $issues += "运行时间过长($([int]$Diagnostics.Uptime.TotalHours)小时)" }
        if (-not $Diagnostics.PipeAvailable) { $issues += "命名管道不可用" }
        
        $summary += "异常 - " + ($issues -join ", ")
    }
    
    return $summary
}

# 主监控循环
Write-Log "=== SystemMonitor 高级监控与自动恢复服务启动 ===" "INFO"
Write-Log "监控目标: $ServiceExe" "INFO"
Write-Log "检查间隔: $CheckIntervalSeconds 秒" "INFO"
Write-Log "内存阈值: $MaxMemoryMB MB" "INFO"
Write-Log "句柄阈值: $MaxHandles" "INFO" 
Write-Log "线程阈值: $MaxThreads" "INFO"
Write-Log "最大运行时间: $MaxUptimeMinutes 分钟" "INFO"
Write-Log "前端连接检查: $EnableFrontendCheck" "INFO"
Write-Log "日志文件: $LogFile" "INFO"

$checkCount = 0
$crashCount = 0
$restartCount = 0
$lastRestartTime = $null

# 主循环
while ($true) {
    $checkCount++
    Write-Log "进行健康检查 #$checkCount" "INFO"
    
    # 检查服务进程
    $serviceProc = Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($ServiceExe)) -ErrorAction SilentlyContinue
    $diagnostics = Get-ServiceDiagnostics -Process $serviceProc
    
    # 检查是否有最近的崩溃记录
    $recentCrashes = Get-EventLogCrashes -HoursBack 1
    if ($recentCrashes.Count -gt 0) {
        $latestCrash = $recentCrashes[0]
        Write-Log "发现最近的崩溃记录: $($latestCrash.TimeCreated)" "WARNING"
        Write-Log $latestCrash.Message "WARNING"
        $crashCount++
    }
    
    # 如果服务未运行，启动它
    if (-not $serviceProc) {
        Write-Log "服务未运行，准备启动" "WARNING"
        $startResult = Start-ServiceProcess -Reason "服务未运行"
        if ($startResult) {
            $restartCount++
            $lastRestartTime = Get-Date
        }
        
        # 等待下一个检查周期
        Start-Sleep -Seconds $CheckIntervalSeconds
        continue
    }
    
    # 输出诊断信息
    $healthSummary = Get-HealthSummary -Diagnostics $diagnostics
    Write-Log $healthSummary -Type $(if ($diagnostics.IsHealthy) { "INFO" } else { "WARNING" })
    
    # 详细信息
    Write-Log "PID: $($diagnostics.PID), 内存: $($diagnostics.MemoryMB)MB, 句柄: $($diagnostics.Handles), 线程: $($diagnostics.Threads)" "INFO"
    Write-Log "运行时间: $([int]$diagnostics.Uptime.TotalHours)小时$([int]$diagnostics.Uptime.Minutes)分钟, 命名管道: $(if ($diagnostics.PipeAvailable) { "可用" } else { "不可用" })" "INFO"
    
    # 检查是否需要重启
    $needsRestart = $false
    $restartReason = ""
    
    # 检查各种条件
    if ($diagnostics.MemoryWarning) {
        $needsRestart = $true
        $restartReason += "内存使用过高($($diagnostics.MemoryMB)MB > ${MaxMemoryMB}MB) "
    }
    
    if ($diagnostics.HandlesWarning) {
        $needsRestart = $true
        $restartReason += "句柄数过多($($diagnostics.Handles) > $MaxHandles) "
    }
    
    if ($diagnostics.ThreadsWarning) {
        $needsRestart = $true
        $restartReason += "线程数过多($($diagnostics.Threads) > $MaxThreads) "
    }
    
    if ($diagnostics.UptimeWarning) {
        $needsRestart = $true
        $restartReason += "运行时间过长($([int]$diagnostics.Uptime.TotalHours)小时 > $([int]$MaxUptimeMinutes/60)小时) "
    }
    
    if (-not $diagnostics.PipeAvailable) {
        $needsRestart = $true
        $restartReason += "命名管道不可用 "
    }
    
    # 如果需要重启，执行重启
    if ($needsRestart) {
        Write-Log "服务需要重启，原因: $restartReason" "WARNING"
        
        # 检查是否过于频繁重启
        if ($lastRestartTime -and ((Get-Date) - $lastRestartTime).TotalMinutes -lt 5) {
            Write-Log "警告: 服务在短时间内频繁重启，这可能表明存在严重问题" "ERROR"
            # 可以在这里添加额外的处理逻辑，例如发送警报
        }
        
        # 尝试重启服务
        $startResult = Start-ServiceProcess -Reason $restartReason
        if ($startResult) {
            $restartCount++
            $lastRestartTime = Get-Date
        }
    }
    
    # 等待下一个检查周期
    Start-Sleep -Seconds $CheckIntervalSeconds
}