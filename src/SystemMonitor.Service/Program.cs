using SystemMonitor.Service;
using SystemMonitor.Service.Services;
using Microsoft.Extensions.Hosting;
using System;
using System.Linq;
using Serilog;
using Serilog.Debugging;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Serilog.Events;
using System.Diagnostics;
using System.Management;
using System.Threading;
using System.Runtime;
using Microsoft.Extensions.DependencyInjection;
using MSLogger = Microsoft.Extensions.Logging.ILogger;

try
{
    Console.WriteLine($"[BOOT] Program start - PID: {Environment.ProcessId}, 工作目录: {Environment.CurrentDirectory}");
    Console.WriteLine($"[BOOT] 命令行参数: {string.Join(" ", args)}");
    
    var serviceOption = new Option<bool>("--service", description: "以 Windows 服务模式运行（用于服务托管场景）");
    var root = new RootCommand("SystemMonitor.Service - Windows 系统监控服务") { serviceOption };
    // 允许传入未声明的参数（测试进程可能注入自定义参数），避免因解析错误导致进程提前退出
    try { root.TreatUnmatchedTokensAsErrors = false; } catch { }

    root.SetHandler((bool isService) =>
    {
        // 仍将完整 args 传入 Host，以便保留默认配置绑定能力
        var hostBuilder = Host.CreateDefaultBuilder(args);
        Console.WriteLine("[BOOT] Host builder created");

        if (isService)
        {
            hostBuilder.UseWindowsService();
        }

        // 解析仓库根目录（向上查找包含标志文件的目录），并确保 logs 目录存在
        static string ResolveRepoRoot()
        {
            try
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    var hasSln = File.Exists(Path.Combine(dir.FullName, "SysSensorV3.sln"));
                    var hasGit = Directory.Exists(Path.Combine(dir.FullName, ".git"));
                    var hasReadme = File.Exists(Path.Combine(dir.FullName, "README.md"));
                    if (hasSln || hasGit || hasReadme)
                    {
                        return dir.FullName;
                    }
                    dir = dir.Parent;
                }
            }
            catch { }
            // 回退到当前工作目录
            return Directory.GetCurrentDirectory();
        }

        // Prefer environment override for log directory if provided
        var envLogDir = Environment.GetEnvironmentVariable("SYS_SENSOR_LOG_DIR");
        string logDir;
        if (!string.IsNullOrWhiteSpace(envLogDir))
        {
            logDir = envLogDir!;
        }
        else
        {
            var repoRoot = ResolveRepoRoot();
            logDir = Path.Combine(repoRoot, "logs");
        }
        try { Directory.CreateDirectory(logDir); } catch { }

        // 启用 Serilog 自诊断到标准错误
        try { SelfLog.Enable(m => Console.Error.WriteLine("[SERILOG] " + m)); } catch { }

        hostBuilder.UseSerilog((ctx, services, lc) =>
        {
            lc.ReadFrom.Configuration(ctx.Configuration)
              .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
              .MinimumLevel.Override("System", LogEventLevel.Warning)
              .Enrich.FromLogContext()
              .WriteTo.File(
                  path: Path.Combine(logDir, "service-.log"),
                  rollingInterval: RollingInterval.Day,
                  outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {MachineName} {ProcessId}/{ThreadId} {SourceContext} {Message:lj}{NewLine}{Exception}");
        });

        hostBuilder.ConfigureServices((ctx, services) =>
        {
            services.AddHostedService<Worker>();
            services.AddSingleton<HistoryStore>();
            services.AddHostedService<RpcHostedService>();
        });

        Console.WriteLine("[BOOT] Building host...");
        var host = hostBuilder.Build();
        Console.WriteLine($"[BOOT] Host built successfully, 进程ID: {Environment.ProcessId}");
        
        if (!isService)
        {
            Console.WriteLine("[SystemMonitor.Service] Running in CONSOLE mode. Use --service to run as Windows Service.");
            Console.WriteLine("按 Ctrl+C 停止服务...");
        }
        else
        {
            Console.WriteLine("[SystemMonitor.Service] Running in WINDOWS SERVICE mode.");
        }
        
        // 注册退出事件处理
        AppDomain.CurrentDomain.ProcessExit += (s, e) => {
            Console.WriteLine($"[SHUTDOWN] ProcessExit 事件触发 - PID: {Environment.ProcessId}");
        };
        
        // 注册未处理异常处理器
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            Console.Error.WriteLine($"[FATAL] 未处理的异常导致进程即将终止，IsTerminating={e.IsTerminating}");
            Console.Error.WriteLine($"[FATAL] 异常: {exception?.Message}");
            Console.Error.WriteLine($"[FATAL] 堆栈: {exception?.StackTrace}");
            
            // 尝试写入Windows事件日志
            try
            {
                using var eventLog = new EventLog("Application");
                eventLog.Source = "SystemMonitor.Service";
                eventLog.WriteEntry($"SystemMonitor服务发生致命错误: {exception?.Message}\n\n堆栈跟踪:\n{exception?.StackTrace}", EventLogEntryType.Error);
            }
            catch { /* 忽略事件日志写入失败 */ }
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Console.Error.WriteLine($"[ERROR] 检测到未观察的任务异常: {e.Exception.Message}");
            e.SetObserved(); // 标记异常已被观察，防止进程终止
        };
        
        Console.CancelKeyPress += (s, e) => {
            Console.WriteLine($"[SHUTDOWN] 收到 Ctrl+C 信号 - PID: {Environment.ProcessId}");
            e.Cancel = false; // 允许退出
        };
        
        // 设置进程监控和崩溃检测
        SetupProcessMonitoring();
        
        // 添加内存压力监控和GC优化
        SetupMemoryMonitoring();
        
        try
        {
            Console.WriteLine("[BOOT] 开始运行 host...");
            host.Run();
            Console.WriteLine("[SHUTDOWN] Host 运行结束");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SHUTDOWN] Host 运行异常: {ex.GetType().Name} - {ex.Message}");
            throw;
        }
    }, serviceOption);

    Console.WriteLine("[BOOT] Invoking root command...");
    var rc = await root.InvokeAsync(args);
    Console.WriteLine($"[SHUTDOWN] Root command completed with return code: {rc}");
    return rc;
}
catch (Exception ex)
{
    try
    {
        Console.Error.WriteLine($"[FATAL] Service crashed - PID: {Environment.ProcessId}");
        Console.Error.WriteLine($"[FATAL] 异常类型: {ex.GetType().Name}");
        Console.Error.WriteLine($"[FATAL] 异常消息: {ex.Message}");
        Console.Error.WriteLine($"[FATAL] 堆栈信息: {ex.StackTrace}");
        
        // 尝试记录内部异常
        var innerEx = ex.InnerException;
        var depth = 0;
        while (innerEx != null && depth < 3)
        {
            depth++;
            Console.Error.WriteLine($"[FATAL] 内部异常{depth}: {innerEx.GetType().Name} - {innerEx.Message}");
            innerEx = innerEx.InnerException;
        }
    }
    catch 
    { 
        // 如果连控制台输出都失败，就静默退出
    }
    return 1;
}
finally
{
    Console.WriteLine($"[SHUTDOWN] Program ending - PID: {Environment.ProcessId}, 退出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
}

static void SetupProcessMonitoring()
{
    try
    {
        // 监控当前进程的性能计数器
        var currentProcess = Process.GetCurrentProcess();
        Console.WriteLine($"[监控] 设置进程监控，进程ID: {currentProcess.Id}，进程名: {currentProcess.ProcessName}，启动时间: {currentProcess.StartTime}");

        // 启动WMI监控线程监控进程终止事件
        var monitoringThread = new Thread(() =>
        {
            try
            {
                var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace WHERE ProcessID = " + currentProcess.Id);
                using var watcher = new ManagementEventWatcher(query);
                
                watcher.EventArrived += (sender, e) =>
                {
                    var exitCode = e.NewEvent["ExitStatus"];
                    Console.WriteLine($"[监控] 检测到进程终止事件，退出代码: {exitCode}");
                };
                
                watcher.Start();
                Console.WriteLine("[监控] WMI进程监控已启动");
                
                // 保持监控线程运行
                while (!currentProcess.HasExited)
                {
                    Thread.Sleep(5000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[监控] WMI进程监控设置失败: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "ProcessMonitor"
        };
        
        monitoringThread.Start();
        
        // 定期记录进程健康状态
        var healthTimer = new Timer(_ =>
        {
            try
            {
                if (!currentProcess.HasExited)
                {
                    var memoryMB = currentProcess.WorkingSet64 / 1024 / 1024;
                    var handleCount = currentProcess.HandleCount;
                    var threadCount = currentProcess.Threads.Count;
                    
                    Console.WriteLine($"[监控] 进程健康检查 - 内存: {memoryMB}MB，句柄: {handleCount}，线程: {threadCount}");
                        
                    // 检查资源泄漏风险
                    if (memoryMB > 500) // 内存超过500MB
                    {
                        Console.WriteLine($"[警告] 内存使用量较高: {memoryMB}MB");
                    }
                    if (handleCount > 1000) // 句柄超过1000
                    {
                        Console.WriteLine($"[警告] 句柄数量较多: {handleCount}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[监控] 进程健康检查失败: {ex.Message}");
            }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[监控] 进程监控设置失败: {ex.Message}");
    }
}

static void SetupMemoryMonitoring()
{
    try
    {
        Console.WriteLine("[监控] 设置内存监控和 GC 优化");
        
        // 设置 GC 参数优化
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        
        // 定期内存监控
        var memoryTimer = new Timer(_ =>
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var memoryMB = process.WorkingSet64 / 1024 / 1024;
                var gcMemoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
                
                Console.WriteLine($"[内存监控] 工作集: {memoryMB}MB, GC内存: {gcMemoryMB}MB, 生代2代收集: {GC.CollectionCount(2)}");
                
                // 如果内存使用过高，强制GC
                if (memoryMB > 800) // 800MB阈值
                {
                    Console.WriteLine($"[警告] 内存使用过高: {memoryMB}MB, 强制执行 GC");
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                
                // 检查内存压力（使用Windows API）
                try
                {
                    var totalMemory = GC.GetTotalMemory(false);
                    var heapSize = GC.GetTotalAllocatedBytes(false);
                    if (heapSize > 500 * 1024 * 1024) // 500MB 的堆分配
                    {
                        Console.WriteLine($"[警告] 堆分配过多: {heapSize / 1024 / 1024}MB");
                    }
                }
                catch { /* 忽略检查失败 */ }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[监控] 内存监控失败: {ex.Message}");
            }
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)); // 每2分钟检查一次
        
        Console.WriteLine("[监控] 内存监控设置完成");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[监控] 内存监控设置失败: {ex.Message}");
    }
}
