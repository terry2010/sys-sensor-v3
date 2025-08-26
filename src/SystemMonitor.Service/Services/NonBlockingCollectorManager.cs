using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SystemMonitor.Service.Services
{
    /// <summary>
    /// 采集器任务状态
    /// </summary>
    public enum CollectorTaskState
    {
        Idle,           // 空闲状态
        Running,        // 正在执行
        Completed,      // 执行完成，有新结果
        TimedOut,       // 超时
        Failed          // 失败
    }

    /// <summary>
    /// 采集器任务信息
    /// </summary>
    public class CollectorTaskInfo
    {
        public string Name { get; set; } = "";
        public CollectorTaskState State { get; set; } = CollectorTaskState.Idle;
        public Task? RunningTask { get; set; }
        public object? LastResult { get; set; }
        public object? PendingResult { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public CancellationTokenSource? TaskCts { get; set; }
        public int ConsecutiveFailures { get; set; }
        public long LastDurationMs { get; set; }
    }

    /// <summary>
    /// 非阻塞采集器管理器
    /// 实现策略：
    /// 1. 如果采集器还在执行，返回上次测量结果
    /// 2. 如果采集器执行完了，返回真正的测量结果  
    /// 3. 如果采集器持续运行超过2秒没有返回结果，杀死它并重新启动
    /// </summary>
    public class NonBlockingCollectorManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, CollectorTaskInfo> _collectorTasks = new();
        private readonly Timer _watchdogTimer;
        private readonly object _lockObj = new object();
        private bool _disposed = false;

        // 配置参数
        private readonly TimeSpan _taskTimeout = TimeSpan.FromSeconds(2);
        private readonly TimeSpan _watchdogInterval = TimeSpan.FromMilliseconds(500);

        public NonBlockingCollectorManager(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            // 启动看门狗定时器，每500ms检查一次任务状态
            _watchdogTimer = new Timer(WatchdogCheck, null, _watchdogInterval, _watchdogInterval);
            
            _logger.LogInformation("非阻塞采集器管理器已启动，任务超时: {Timeout}ms", _taskTimeout.TotalMilliseconds);
        }

        /// <summary>
        /// 尝试获取采集器结果
        /// </summary>
        /// <param name="collectorName">采集器名称</param>
        /// <param name="collector">采集器实例</param>
        /// <param name="result">输出结果</param>
        /// <returns>是否成功获取结果</returns>
        public bool TryGetResult(string collectorName, Func<object?> collector, out object? result)
        {
            result = null;

            if (string.IsNullOrEmpty(collectorName) || collector == null)
                return false;

            var taskInfo = _collectorTasks.GetOrAdd(collectorName, _ => new CollectorTaskInfo 
            { 
                Name = collectorName, 
                State = CollectorTaskState.Idle 
            });

            lock (_lockObj)
            {
                switch (taskInfo.State)
                {
                    case CollectorTaskState.Idle:
                        // 空闲状态，启动新的采集任务
                        StartCollectorTask(taskInfo, collector);
                        result = taskInfo.LastResult; // 返回上次结果（可能为null）
                        return taskInfo.LastResult != null;

                    case CollectorTaskState.Running:
                        // 正在执行，返回上次结果
                        result = taskInfo.LastResult;
                        _logger.LogDebug("采集器 {Name} 正在执行中，返回上次结果", collectorName);
                        return taskInfo.LastResult != null;

                    case CollectorTaskState.Completed:
                        // 执行完成，返回新结果并重置状态
                        result = taskInfo.PendingResult;
                        taskInfo.LastResult = taskInfo.PendingResult;
                        taskInfo.PendingResult = null;
                        taskInfo.State = CollectorTaskState.Idle;
                        taskInfo.ConsecutiveFailures = 0; // 重置失败计数
                        _logger.LogDebug("采集器 {Name} 执行完成，返回新结果，耗时: {Duration}ms", 
                            collectorName, taskInfo.LastDurationMs);
                        return result != null;

                    case CollectorTaskState.TimedOut:
                    case CollectorTaskState.Failed:
                        // 超时或失败，清理并重新启动
                        CleanupTask(taskInfo);
                        StartCollectorTask(taskInfo, collector);
                        result = taskInfo.LastResult; // 返回上次已知的好结果
                        return taskInfo.LastResult != null;

                    default:
                        _logger.LogWarning("采集器 {Name} 处于未知状态: {State}", collectorName, taskInfo.State);
                        result = taskInfo.LastResult;
                        return taskInfo.LastResult != null;
                }
            }
        }

        /// <summary>
        /// 启动采集器任务
        /// </summary>
        private void StartCollectorTask(CollectorTaskInfo taskInfo, Func<object?> collector)
        {
            try
            {
                taskInfo.TaskCts?.Cancel();
                taskInfo.TaskCts = new CancellationTokenSource();
                taskInfo.State = CollectorTaskState.Running;
                taskInfo.StartTime = DateTime.UtcNow;
                taskInfo.LastUpdateTime = DateTime.UtcNow;

                var sw = Stopwatch.StartNew();
                taskInfo.RunningTask = Task.Run(() =>
                {
                    try
                    {
                        var result = collector();
                        sw.Stop();

                        lock (_lockObj)
                        {
                            if (taskInfo.TaskCts?.Token.IsCancellationRequested != true)
                            {
                                taskInfo.PendingResult = result;
                                taskInfo.LastDurationMs = sw.ElapsedMilliseconds;
                                taskInfo.State = CollectorTaskState.Completed;
                                taskInfo.LastUpdateTime = DateTime.UtcNow;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        _logger.LogWarning(ex, "采集器 {Name} 执行异常", taskInfo.Name);
                        
                        lock (_lockObj)
                        {
                            if (taskInfo.TaskCts?.Token.IsCancellationRequested != true)
                            {
                                taskInfo.State = CollectorTaskState.Failed;
                                taskInfo.ConsecutiveFailures++;
                                taskInfo.LastUpdateTime = DateTime.UtcNow;
                            }
                        }
                    }
                }, taskInfo.TaskCts.Token);

                _logger.LogDebug("已启动采集器任务: {Name}", taskInfo.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动采集器任务失败: {Name}", taskInfo.Name);
                taskInfo.State = CollectorTaskState.Failed;
                taskInfo.ConsecutiveFailures++;
            }
        }

        /// <summary>
        /// 清理任务资源
        /// </summary>
        private void CleanupTask(CollectorTaskInfo taskInfo)
        {
            try
            {
                taskInfo.TaskCts?.Cancel();
                
                // 等待任务结束，但不超过100ms
                if (taskInfo.RunningTask != null && !taskInfo.RunningTask.IsCompleted)
                {
                    if (!taskInfo.RunningTask.Wait(100))
                    {
                        _logger.LogWarning("采集器任务 {Name} 未能及时结束", taskInfo.Name);
                    }
                }
                
                taskInfo.TaskCts?.Dispose();
                taskInfo.TaskCts = null;
                taskInfo.RunningTask = null;
                
                _logger.LogDebug("已清理采集器任务: {Name}", taskInfo.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "清理采集器任务异常: {Name}", taskInfo.Name);
            }
        }

        /// <summary>
        /// 看门狗检查，处理超时任务
        /// </summary>
        private void WatchdogCheck(object? state)
        {
            try
            {
                var now = DateTime.UtcNow;
                var timedOutTasks = new List<CollectorTaskInfo>();

                // 收集超时任务
                foreach (var kvp in _collectorTasks)
                {
                    var taskInfo = kvp.Value;
                    
                    if (taskInfo.State == CollectorTaskState.Running)
                    {
                        var elapsed = now - taskInfo.StartTime;
                        if (elapsed > _taskTimeout)
                        {
                            timedOutTasks.Add(taskInfo);
                        }
                    }
                }

                // 处理超时任务
                foreach (var taskInfo in timedOutTasks)
                {
                    lock (_lockObj)
                    {
                        if (taskInfo.State == CollectorTaskState.Running)
                        {
                            var elapsed = now - taskInfo.StartTime;
                            _logger.LogWarning("采集器 {Name} 超时 ({Elapsed}ms > {Timeout}ms)，强制终止任务", 
                                taskInfo.Name, elapsed.TotalMilliseconds, _taskTimeout.TotalMilliseconds);
                            
                            taskInfo.State = CollectorTaskState.TimedOut;
                            taskInfo.ConsecutiveFailures++;
                            CleanupTask(taskInfo);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "看门狗检查异常");
            }
        }

        /// <summary>
        /// 获取采集器统计信息
        /// </summary>
        public Dictionary<string, object> GetStatistics()
        {
            var stats = new Dictionary<string, object>();
            
            foreach (var kvp in _collectorTasks)
            {
                var taskInfo = kvp.Value;
                stats[taskInfo.Name] = new
                {
                    state = taskInfo.State.ToString(),
                    consecutive_failures = taskInfo.ConsecutiveFailures,
                    last_duration_ms = taskInfo.LastDurationMs,
                    last_update = taskInfo.LastUpdateTime,
                    has_result = taskInfo.LastResult != null
                };
            }
            
            return stats;
        }

        public void Dispose()
        {
            if (_disposed) return;
            
            _disposed = true;
            _watchdogTimer?.Dispose();

            // 清理所有任务
            foreach (var kvp in _collectorTasks)
            {
                CleanupTask(kvp.Value);
            }
            
            _collectorTasks.Clear();
            _logger.LogInformation("非阻塞采集器管理器已销毁");
        }
    }
}