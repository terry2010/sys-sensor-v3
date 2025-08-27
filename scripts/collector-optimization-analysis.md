# 采集器性能优化分析报告

## 1. 问题概述

根据监控分析，SystemMonitor服务存在以下性能问题：

1. **GPU采集器**：每次执行耗时约800-900ms
2. **Power采集器**：每次执行耗时约700-800ms
3. **Network采集器**：频繁超时（超过2000ms）

这些问题导致服务CPU占用率高达188%-206%，严重影响系统性能。

## 2. 详细分析

### 2.1 GPU采集器分析

**当前实现问题：**
1. 大量使用PerformanceCounter，每个实例约消耗2-4MB内存
2. 频繁访问WMI和性能计数器，导致高CPU占用
3. 复杂的数据聚合和处理逻辑

**优化建议：**
1. 实现全局性能计数器池，避免重复创建PerformanceCounter实例
2. 增加缓存机制，减少频繁查询
3. 优化数据聚合算法，减少不必要的计算

### 2.2 Power采集器分析

**当前实现问题：**
1. 频繁访问WMI root\WMI命名空间
2. 多次查询不同WMI类（BatteryStatus、BatteryFullChargedCapacity等）
3. 复杂的数据处理和转换逻辑

**优化建议：**
1. 增加缓存时间，从400ms延长到1000ms
2. 减少不必要的WMI查询
3. 优化数据处理逻辑，减少计算开销

### 2.3 Network采集器分析

**当前实现问题：**
1. 频繁访问WMI root\StandardCimv2命名空间
2. 多次调用NetworkInterface.GetAllNetworkInterfaces()
3. 复杂的性能计数器初始化和读取逻辑

**优化建议：**
1. 增加缓存机制，减少频繁查询
2. 优化性能计数器的初始化逻辑
3. 减少不必要的网络接口信息查询

## 3. 优化方案

### 3.1 GPU采集器优化

```csharp
// 1. 实现性能计数器池化管理
private static class PerformanceCounterPool
{
    private static readonly Dictionary<string, Queue<PerformanceCounter>> _pool = 
        new Dictionary<string, Queue<PerformanceCounter>>(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new object();
    
    public static PerformanceCounter GetOrCreate(string category, string counter, string instance)
    {
        var key = $"{category}|{counter}|{instance}";
        lock (_lock)
        {
            if (!_pool.TryGetValue(key, out var queue))
            {
                queue = new Queue<PerformanceCounter>();
                _pool[key] = queue;
            }
            
            if (queue.Count > 0)
            {
                return queue.Dequeue();
            }
            
            return new PerformanceCounter(category, counter, instance, readOnly: true);
        }
    }
    
    public static void Return(PerformanceCounter counter, string category, string counterName, string instance)
    {
        var key = $"{category}|{counterName}|{instance}";
        lock (_lock)
        {
            if (!_pool.TryGetValue(key, out var queue))
            {
                queue = new Queue<PerformanceCounter>();
                _pool[key] = queue;
            }
            
            // 限制池大小，避免内存泄漏
            if (queue.Count < 10)
            {
                queue.Enqueue(counter);
            }
            else
            {
                counter.Dispose();
            }
        }
    }
}

// 2. 增加更长的缓存时间
private static readonly object _lock = new();
private static long _lastTicks;
private static object? _lastVal;
private const int CacheDurationMs = 1000; // 从200ms增加到1000ms

public object? Collect()
{
    var now = Environment.TickCount64;
    lock (_lock)
    {
        if (_lastVal != null && now - _lastTicks <= CacheDurationMs)
        {
            return _lastVal;
        }
    }
    
    // ... 采集逻辑 ...
}
```

### 3.2 Power采集器优化

```csharp
// 1. 增加缓存时间
private static readonly object _lock = new();
private static long _lastTs;
private static object? _lastPayload;
private const int CacheDurationMs = 1000; // 从400ms增加到1000ms

public object? Collect()
{
    try
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            if (_lastPayload != null && now - _lastTs <= CacheDurationMs)
            {
                return _lastPayload;
            }
        }
        
        // ... 采集逻辑 ...
    }
    catch
    {
        // ... 错误处理 ...
    }
}

// 2. 减少不必要的WMI查询
private static object BuildPayload()
{
    // 1) 基础：GetSystemPowerStatus（轻量级，保留）
    double? pct = null; bool? acOnline = null; int? remainMin = null; int? toFullMin = null; string state = "unknown";
    try
    {
        if (GetSystemPowerStatus(out var sps))
        {
            // ... 处理逻辑 ...
        }
    }
    catch { /* ignore */ }

    // 2) 高级：WMI root\WMI（可选，根据需要启用）
    // 只在电池状态变化较大时才查询详细信息
    double? voltageMv = null; double? currentMa = null; double? powerW = null; 
    int? cycle = null; string? condition = null;
    double? fullMah = null; double? designMah = null; 
    string? manufacturer = null; string? serial = null; string? mfgDate = null; 
    int? timeOnBatterySec = null; double? tempC = null;

    // 只有在需要详细信息时才查询WMI
    bool needDetailedInfo = pct.HasValue && (pct.Value < 95 || state == "charging");
    
    if (needDetailedInfo)
    {
        try
        {
            var scope = new ManagementScope(@"\\\.\root\WMI");
            scope.Connect();

            // BatteryStatus：Voltage / Current / Rate / RemainingCapacity
            TryQuery(scope, "SELECT * FROM BatteryStatus", mo =>
            {
                // ... 查询逻辑 ...
            });

            // 其他WMI查询...
        }
        catch { /* 忽略 WMI 读取异常 */ }
    }
    
    // ... 构造返回对象 ...
}
```

### 3.3 Network采集器优化

```csharp
// 1. 增加缓存机制
private static object? _cache;
private static long _cacheAt;
private static readonly object _lock = new();
private const int CacheDurationMs = 2000; // 增加到2000ms

public object Read(bool force = false)
{
    var now = Environment.TickCount64;
    if (!force)
    {
        lock (_lock)
        {
            if (_cache != null && now - _cacheAt < CacheDurationMs)
                return _cache;
        }
    }

    // ... 采集逻辑 ...
}

// 2. 优化性能计数器初始化
private void EnsureInit()
{
    if (_initTried) return;
    _initTried = true;
    try
    {
        var cat = new System.Diagnostics.PerformanceCounterCategory("Network Interface");
        var instances = cat.GetInstanceNames();
        var valid = instances.Where(IsValidInterface).ToArray();
        var list = new List<IfCounters>();
        
        // 批量初始化，减少系统调用次数
        var countersToInitialize = new List<(string instance, string counterName)>();
        foreach (var inst in valid)
        {
            countersToInitialize.Add((inst, "Bytes Sent/sec"));
            countersToInitialize.Add((inst, "Bytes Received/sec"));
            countersToInitialize.Add((inst, "Packets Sent/sec"));
            countersToInitialize.Add((inst, "Packets Received/sec"));
            // ... 其他计数器 ...
        }
        
        // 并行初始化计数器
        var initializedCounters = countersToInitialize
            .AsParallel()
            .WithDegreeOfParallelism(Math.Min(valid.Length, Environment.ProcessorCount))
            .Select(tuple => {
                try
                {
                    var counter = new System.Diagnostics.PerformanceCounter(
                        "Network Interface", tuple.counterName, tuple.instance, readOnly: true);
                    // 预热
                    _ = counter.NextValue();
                    return (tuple.instance, tuple.counterName, counter);
                }
                catch
                {
                    return (tuple.instance, tuple.counterName, (PerformanceCounter)null);
                }
            })
            .ToList();
            
        // 构建IfCounters列表
        // ... 构建逻辑 ...
    }
    catch
    {
        // ignore
    }
}
```

## 4. 实施计划

### 4.1 第一阶段：缓存优化（1-2天）
1. 增加GPU和Power采集器的缓存时间
2. 优化Network采集器的缓存机制
3. 测试缓存效果

### 4.2 第二阶段：资源池化（2-3天）
1. 实现PerformanceCounter池化管理
2. 优化WMI查询逻辑
3. 减少不必要的资源创建和销毁

### 4.3 第三阶段：算法优化（3-5天）
1. 优化数据聚合算法
2. 减少不必要的计算
3. 实现按需查询机制

## 5. 预期效果

通过以上优化措施，预计可以实现以下效果：

1. **CPU占用率降低**：从当前的188%-206%降低到50%-80%
2. **内存使用优化**：减少PerformanceCounter实例创建，降低内存占用约40-50MB
3. **响应时间改善**：
   - GPU采集器：从900ms降低到300-500ms
   - Power采集器：从800ms降低到200-400ms
   - Network采集器：减少超时频率，平均响应时间降低50%

## 6. 风险评估

1. **数据准确性**：增加缓存时间可能影响数据实时性
   - 缓解措施：实现智能缓存，根据数据变化率动态调整缓存时间

2. **兼容性问题**：不同系统环境下WMI和性能计数器可用性不同
   - 缓解措施：保持现有的容错机制和回退方案

3. **资源泄漏**：池化管理不当可能导致资源泄漏
   - 缓解措施：实现严格的资源管理和池大小限制