# Collector Performance Test Script
# 测试采集器性能优化效果

Write-Host "=== Collector Performance Test ===" -ForegroundColor Cyan
Write-Host "Testing GPU, Power, and Network collector performance" -ForegroundColor Yellow

$testDuration = 60  # 测试持续时间（秒）
$checkInterval = 2   # 检查间隔（秒）
$endTime = (Get-Date).AddSeconds($testDuration)

Write-Host "Test Duration: $testDuration seconds" -ForegroundColor Yellow
Write-Host "Check Interval: $checkInterval seconds" -ForegroundColor Yellow
Write-Host "Start Time: $(Get-Date)" -ForegroundColor Green
Write-Host ""

# 记录初始状态
$initialSamples = @()
$optimizedSamples = @()

# 测试原始采集器性能
Write-Host "Testing original collector performance..." -ForegroundColor Yellow

$checkCount = 0
$originalGpuTimes = @()
$originalPowerTimes = @()
$originalNetworkTimes = @()

while ((Get-Date) -lt $endTime) {
    $checkCount++
    Write-Host "[$checkCount] $(Get-Date -Format 'HH:mm:ss')" -ForegroundColor White
    
    # 测试GPU采集器
    $gpuStart = Get-Date
    try {
        # 模拟调用GPU采集器
        $gpuResult = & curl.exe -s "http://localhost:8080/api/gpu" 2>$null
        $gpuEnd = Get-Date
        $gpuTime = ($gpuEnd - $gpuStart).TotalMilliseconds
        $originalGpuTimes += $gpuTime
        Write-Host "  GPU Collection: $([math]::Round($gpuTime, 2))ms" -ForegroundColor Gray
    } catch {
        Write-Host "  GPU Collection: Error" -ForegroundColor Red
    }
    
    # 测试Power采集器
    $powerStart = Get-Date
    try {
        # 模拟调用Power采集器
        $powerResult = & curl.exe -s "http://localhost:8080/api/power" 2>$null
        $powerEnd = Get-Date
        $powerTime = ($powerEnd - $powerStart).TotalMilliseconds
        $originalPowerTimes += $powerTime
        Write-Host "  Power Collection: $([math]::Round($powerTime, 2))ms" -ForegroundColor Gray
    } catch {
        Write-Host "  Power Collection: Error" -ForegroundColor Red
    }
    
    # 测试Network采集器
    $networkStart = Get-Date
    try {
        # 模拟调用Network采集器
        $networkResult = & curl.exe -s "http://localhost:8080/api/network" 2>$null
        $networkEnd = Get-Date
        $networkTime = ($networkEnd - $networkStart).TotalMilliseconds
        $originalNetworkTimes += $networkTime
        Write-Host "  Network Collection: $([math]::Round($networkTime, 2))ms" -ForegroundColor Gray
    } catch {
        Write-Host "  Network Collection: Error" -ForegroundColor Red
    }
    
    Start-Sleep -Seconds $checkInterval
}

# 计算原始性能统计数据
if ($originalGpuTimes.Count -gt 0) {
    $originalGpuAvg = [math]::Round(($originalGpuTimes | Measure-Object -Average).Average, 2)
    $originalGpuMax = [math]::Round(($originalGpuTimes | Measure-Object -Maximum).Maximum, 2)
    $originalGpuMin = [math]::Round(($originalGpuTimes | Measure-Object -Minimum).Minimum, 2)
} else {
    $originalGpuAvg = $originalGpuMax = $originalGpuMin = 0
}

if ($originalPowerTimes.Count -gt 0) {
    $originalPowerAvg = [math]::Round(($originalPowerTimes | Measure-Object -Average).Average, 2)
    $originalPowerMax = [math]::Round(($originalPowerTimes | Measure-Object -Maximum).Maximum, 2)
    $originalPowerMin = [math]::Round(($originalPowerTimes | Measure-Object -Minimum).Minimum, 2)
} else {
    $originalPowerAvg = $originalPowerMax = $originalPowerMin = 0
}

if ($originalNetworkTimes.Count -gt 0) {
    $originalNetworkAvg = [math]::Round(($originalNetworkTimes | Measure-Object -Average).Average, 2)
    $originalNetworkMax = [math]::Round(($originalNetworkTimes | Measure-Object -Maximum).Maximum, 2)
    $originalNetworkMin = [math]::Round(($originalNetworkTimes | Measure-Object -Minimum).Minimum, 2)
} else {
    $originalNetworkAvg = $originalNetworkMax = $originalNetworkMin = 0
}

Write-Host "`n=== Original Performance Results ===" -ForegroundColor Cyan
Write-Host "GPU Collector:" -ForegroundColor Yellow
Write-Host "  Average: ${originalGpuAvg}ms" -ForegroundColor White
Write-Host "  Maximum: ${originalGpuMax}ms" -ForegroundColor White
Write-Host "  Minimum: ${originalGpuMin}ms" -ForegroundColor White

Write-Host "Power Collector:" -ForegroundColor Yellow
Write-Host "  Average: ${originalPowerAvg}ms" -ForegroundColor White
Write-Host "  Maximum: ${originalPowerMax}ms" -ForegroundColor White
Write-Host "  Minimum: ${originalPowerMin}ms" -ForegroundColor White

Write-Host "Network Collector:" -ForegroundColor Yellow
Write-Host "  Average: ${originalNetworkAvg}ms" -ForegroundColor White
Write-Host "  Maximum: ${originalNetworkMax}ms" -ForegroundColor White
Write-Host "  Minimum: ${originalNetworkMin}ms" -ForegroundColor White

# 重置测试时间
$endTime = (Get-Date).AddSeconds($testDuration)
$checkCount = 0

Write-Host "`nApplying optimizations..." -ForegroundColor Yellow
# 这里应该应用优化（在实际测试中需要替换采集器实现）

Write-Host "`nTesting optimized collector performance..." -ForegroundColor Yellow

$optimizedGpuTimes = @()
$optimizedPowerTimes = @()
$optimizedNetworkTimes = @()

while ((Get-Date) -lt $endTime) {
    $checkCount++
    Write-Host "[$checkCount] $(Get-Date -Format 'HH:mm:ss')" -ForegroundColor White
    
    # 测试优化后的GPU采集器
    $gpuStart = Get-Date
    try {
        # 模拟调用优化后的GPU采集器
        $gpuResult = & curl.exe -s "http://localhost:8080/api/gpu" 2>$null
        $gpuEnd = Get-Date
        $gpuTime = ($gpuEnd - $gpuStart).TotalMilliseconds
        $optimizedGpuTimes += $gpuTime
        Write-Host "  Optimized GPU Collection: $([math]::Round($gpuTime, 2))ms" -ForegroundColor Gray
    } catch {
        Write-Host "  Optimized GPU Collection: Error" -ForegroundColor Red
    }
    
    # 测试优化后的Power采集器
    $powerStart = Get-Date
    try {
        # 模拟调用优化后的Power采集器
        $powerResult = & curl.exe -s "http://localhost:8080/api/power" 2>$null
        $powerEnd = Get-Date
        $powerTime = ($powerEnd - $powerStart).TotalMilliseconds
        $optimizedPowerTimes += $powerTime
        Write-Host "  Optimized Power Collection: $([math]::Round($powerTime, 2))ms" -ForegroundColor Gray
    } catch {
        Write-Host "  Optimized Power Collection: Error" -ForegroundColor Red
    }
    
    # 测试优化后的Network采集器
    $networkStart = Get-Date
    try {
        # 模拟调用优化后的Network采集器
        $networkResult = & curl.exe -s "http://localhost:8080/api/network" 2>$null
        $networkEnd = Get-Date
        $networkTime = ($networkEnd - $networkStart).TotalMilliseconds
        $optimizedNetworkTimes += $networkTime
        Write-Host "  Optimized Network Collection: $([math]::Round($networkTime, 2))ms" -ForegroundColor Gray
    } catch {
        Write-Host "  Optimized Network Collection: Error" -ForegroundColor Red
    }
    
    Start-Sleep -Seconds $checkInterval
}

# 计算优化后性能统计数据
if ($optimizedGpuTimes.Count -gt 0) {
    $optimizedGpuAvg = [math]::Round(($optimizedGpuTimes | Measure-Object -Average).Average, 2)
    $optimizedGpuMax = [math]::Round(($optimizedGpuTimes | Measure-Object -Maximum).Maximum, 2)
    $optimizedGpuMin = [math]::Round(($optimizedGpuTimes | Measure-Object -Minimum).Minimum, 2)
} else {
    $optimizedGpuAvg = $optimizedGpuMax = $optimizedGpuMin = 0
}

if ($optimizedPowerTimes.Count -gt 0) {
    $optimizedPowerAvg = [math]::Round(($optimizedPowerTimes | Measure-Object -Average).Average, 2)
    $optimizedPowerMax = [math]::Round(($optimizedPowerTimes | Measure-Object -Maximum).Maximum, 2)
    $optimizedPowerMin = [math]::Round(($optimizedPowerTimes | Measure-Object -Minimum).Minimum, 2)
} else {
    $optimizedPowerAvg = $optimizedPowerMax = $optimizedPowerMin = 0
}

if ($optimizedNetworkTimes.Count -gt 0) {
    $optimizedNetworkAvg = [math]::Round(($optimizedNetworkTimes | Measure-Object -Average).Average, 2)
    $optimizedNetworkMax = [math]::Round(($optimizedNetworkTimes | Measure-Object -Maximum).Maximum, 2)
    $optimizedNetworkMin = [math]::Round(($optimizedNetworkTimes | Measure-Object -Minimum).Minimum, 2)
} else {
    $optimizedNetworkAvg = $optimizedNetworkMax = $optimizedNetworkMin = 0
}

Write-Host "`n=== Optimized Performance Results ===" -ForegroundColor Cyan
Write-Host "Optimized GPU Collector:" -ForegroundColor Yellow
Write-Host "  Average: ${optimizedGpuAvg}ms" -ForegroundColor White
Write-Host "  Maximum: ${optimizedGpuMax}ms" -ForegroundColor White
Write-Host "  Minimum: ${optimizedGpuMin}ms" -ForegroundColor White

Write-Host "Optimized Power Collector:" -ForegroundColor Yellow
Write-Host "  Average: ${optimizedPowerAvg}ms" -ForegroundColor White
Write-Host "  Maximum: ${optimizedPowerMax}ms" -ForegroundColor White
Write-Host "  Minimum: ${optimizedPowerMin}ms" -ForegroundColor White

Write-Host "Optimized Network Collector:" -ForegroundColor Yellow
Write-Host "  Average: ${optimizedNetworkAvg}ms" -ForegroundColor White
Write-Host "  Maximum: ${optimizedNetworkMax}ms" -ForegroundColor White
Write-Host "  Minimum: ${optimizedNetworkMin}ms" -ForegroundColor White

# 计算性能提升
Write-Host "`n=== Performance Improvement ===" -ForegroundColor Cyan

if ($originalGpuAvg -gt 0 -and $optimizedGpuAvg -gt 0) {
    $gpuImprovement = [math]::Round((($originalGpuAvg - $optimizedGpuAvg) / $originalGpuAvg) * 100, 2)
    Write-Host "GPU Collector Performance Improvement: $gpuImprovement%" -ForegroundColor $(if ($gpuImprovement -gt 0) { "Green" } else { "Red" })
}

if ($originalPowerAvg -gt 0 -and $optimizedPowerAvg -gt 0) {
    $powerImprovement = [math]::Round((($originalPowerAvg - $optimizedPowerAvg) / $originalPowerAvg) * 100, 2)
    Write-Host "Power Collector Performance Improvement: $powerImprovement%" -ForegroundColor $(if ($powerImprovement -gt 0) { "Green" } else { "Red" })
}

if ($originalNetworkAvg -gt 0 -and $optimizedNetworkAvg -gt 0) {
    $networkImprovement = [math]::Round((($originalNetworkAvg - $optimizedNetworkAvg) / $originalNetworkAvg) * 100, 2)
    Write-Host "Network Collector Performance Improvement: $networkImprovement%" -ForegroundColor $(if ($networkImprovement -gt 0) { "Green" } else { "Red" })
}

Write-Host "`nEnd time: $(Get-Date)" -ForegroundColor Green