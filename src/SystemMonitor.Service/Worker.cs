namespace SystemMonitor.Service;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[worker] Worker 服务启动，进程ID: {ProcessId}，线程ID: {ThreadId}", Environment.ProcessId, Environment.CurrentManagedThreadId);
        
        // 注册退出事件处理
        stoppingToken.Register(() => {
            _logger.LogInformation("[worker] 收到停止信号，Worker 服务开始关闭流程");
        });
        
        var loopCount = 0;
        var startTime = DateTimeOffset.Now;
        
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                loopCount++;
                
                // 每1000次循环记录一次详细信息
                if (loopCount % 1000 == 0)
                {
                    var uptime = DateTimeOffset.Now - startTime;
                    _logger.LogInformation("[worker] Worker 运行状态: 循环次数={LoopCount}, 运行时间={Uptime}, 当前时间={Time}", 
                        loopCount, uptime, DateTimeOffset.Now);
                }
                else if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("[worker] Worker running at: {time}", DateTimeOffset.Now);
                }
                
                try
                {
                    await Task.Delay(1000, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("[worker] Worker 延迟被取消，准备退出");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[worker] Worker 执行异常，异常类型: {ExceptionType}，消息: {Message}", ex.GetType().Name, ex.Message);
            throw;
        }
        finally
        {
            var totalUptime = DateTimeOffset.Now - startTime;
            _logger.LogInformation("[worker] Worker 服务已停止，总运行时间: {TotalUptime}，总循环次数: {TotalLoops}", totalUptime, loopCount);
        }
    }
}
