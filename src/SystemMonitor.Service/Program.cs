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

        // 确保日志目录存在（相对于工作目录）
        try { Directory.CreateDirectory("logs"); } catch { }

        // 启用 Serilog 自诊断到标准错误
        try { SelfLog.Enable(m => Console.Error.WriteLine("[SERILOG] " + m)); } catch { }

        hostBuilder.UseSerilog((ctx, services, lc) =>
        {
            lc.ReadFrom.Configuration(ctx.Configuration);
        });

        hostBuilder.ConfigureServices((ctx, services) =>
        {
            services.AddHostedService<Worker>();
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
