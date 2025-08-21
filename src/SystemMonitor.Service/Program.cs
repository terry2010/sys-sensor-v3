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

try
{
    Console.WriteLine("[BOOT] Program start");
    var serviceOption = new Option<bool>("--service", description: "以 Windows 服务模式运行（用于服务托管场景）");
    var root = new RootCommand("SystemMonitor.Service - Windows 系统监控服务") { serviceOption };

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

        var repoRoot = ResolveRepoRoot();
        var logDir = Path.Combine(repoRoot, "logs");
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
        Console.WriteLine("[BOOT] Host built");
        if (!isService)
        {
            Console.WriteLine("[SystemMonitor.Service] Running in CONSOLE mode. Use --service to run as Windows Service.");
        }

        host.Run();
    }, serviceOption);

    Console.WriteLine("[BOOT] Invoking root command...");
    var rc = await root.InvokeAsync(args);
    Console.WriteLine($"[BOOT] Root command returned: {rc}");
    return rc;
}
catch (Exception ex)
{
    try
    {
        Console.Error.WriteLine("[FATAL] Service crashed: " + ex.ToString());
    }
    catch { }
    return 1;
}
