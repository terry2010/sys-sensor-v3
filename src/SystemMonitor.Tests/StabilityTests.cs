using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;

namespace SystemMonitor.Tests;

// 将 E2E/稳定性测试放入同一集合，避免并行运行互相争抢命名管道/服务进程
[CollectionDefinition("E2E", DisableParallelization = true)]
public class E2ECollection : ICollectionFixture<object> { }

[SupportedOSPlatform("windows")]
[Collection("E2E")]
public class StabilityTests : IAsyncLifetime
{
    private Process? _svc;
    private const string PipeName = "sys_sensor_v3.rpc";

    // Win32 API: WaitNamedPipeW
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WaitNamedPipe(string lpNamedPipeName, uint nTimeOut);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var sln = Path.Combine(dir.FullName, "SysSensorV3.sln");
            var csproj = Path.Combine(dir.FullName, "src", "SystemMonitor.Service", "SystemMonitor.Service.csproj");
            if (File.Exists(sln) || File.Exists(csproj)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root not found by anchor files");
    }

    private static string? FindServiceBinary()
    {
        var useBin = string.Equals(Environment.GetEnvironmentVariable("E2E_USE_BIN"), "1", StringComparison.Ordinal);
        if (!useBin) return null;
        var root = FindRepoRoot();
        var exe = Path.Combine(root, "src", "SystemMonitor.Service", "bin", "Release", "net8.0", "SystemMonitor.Service.exe");
        if (File.Exists(exe)) return exe;
        var dll = Path.Combine(root, "src", "SystemMonitor.Service", "bin", "Release", "net8.0", "SystemMonitor.Service.dll");
        if (File.Exists(dll)) return dll;
        return null;
    }

    private static async Task<JsonRpc> ConnectAsync(TimeSpan timeout, object? localTarget = null)
    {
        var start = DateTime.UtcNow;
        Exception? last = null;
        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(2000).ConfigureAwait(false);
                var reader = client.UsePipeReader();
                var writer = client.UsePipeWriter();
                var formatter = new SystemTextJsonFormatter();
                formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                var handler = new HeaderDelimitedMessageHandler(writer, reader, formatter);
                var rpc = new JsonRpc(handler);
                if (localTarget != null) rpc.AddLocalRpcTarget(localTarget);
                rpc.StartListening();
                return rpc;
            }
            catch (Exception ex)
            {
                last = ex; await Task.Delay(200).ConfigureAwait(false);
            }
        }
        throw new TimeoutException($"Failed to connect named pipe within {timeout}. Last error: {last}");
    }

    private static async Task WaitPipeReadyAsync(TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        var pipePath = @"\\.\pipe\" + PipeName;
        while (DateTime.UtcNow - start < timeout)
        {
            if (WaitNamedPipe(pipePath, 500)) return;
            await Task.Delay(200).ConfigureAwait(false);
        }
        throw new TimeoutException($"Service pipe not ready within {timeout}");
    }

    public async Task InitializeAsync()
    {
        // 清理同名残留服务进程
        try
        {
            foreach (var p in Process.GetProcessesByName("SystemMonitor.Service"))
            {
                try { p.Kill(true); p.WaitForExit(2000); } catch { }
            }
        }
        catch { }

        var bin = FindServiceBinary();
        var root = FindRepoRoot();
        ProcessStartInfo psi;
        if (bin != null)
        {
            var isDll = bin.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            var binDir = Path.GetDirectoryName(bin)!;
            psi = new ProcessStartInfo
            {
                FileName = isDll ? "dotnet" : bin,
                Arguments = isDll ? $"\"{bin}\"" : string.Empty,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = binDir,
            };
        }
        else
        {
            var proj = Path.Combine(root, "src", "SystemMonitor.Service");
            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{proj}\" -c Release",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };
        }
        _svc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start service process");
        // 不阻塞读取（防止缓冲区占满）
        try { _svc.BeginOutputReadLine(); _svc.BeginErrorReadLine(); } catch { }
        await Task.Delay(500);
        if (_svc.HasExited) throw new InvalidOperationException($"Service exited early: code={_svc.ExitCode}");
        await WaitPipeReadyAsync(TimeSpan.FromSeconds(90));
    }

    public Task DisposeAsync()
    {
        try { if (_svc != null && !_svc.HasExited) { _svc.Kill(true); _svc.WaitForExit(2000); } } catch { }
        return Task.CompletedTask;
    }

    private sealed class MetricsProbe
    {
        private readonly object _lock = new();
        public System.Text.Json.JsonElement? LastPayload { get; private set; }
        public int Count { get; private set; }
        [JsonRpcMethod("metrics")]
        public void OnMetrics(System.Text.Json.JsonElement payload)
        {
            lock (_lock) { LastPayload = payload; Count++; }
        }
    }

    [Fact]
    public async Task Startup_WarmingUp_Then_Payload_Arrives()
    {
        var probe = new MetricsProbe();
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(25), probe);
        var helloParams = new { app_version = "1.0.0", protocol_version = 1, token = "t", capabilities = new[] { "metrics_stream" } };
        await rpc.InvokeAsync<object>("hello", new object[] { helloParams });

        // 显式订阅（双保险）
        var sub = await rpc.InvokeAsync<JsonElement>("subscribe_metrics", new object[] { new { enable = true } });
        Assert.True(sub.TryGetProperty("ok", out var okEl) ? okEl.GetBoolean() : true);

        // 等待至多 8s 收到至少一条
        var start = DateTime.UtcNow; JsonElement? p = null;
        while ((DateTime.UtcNow - start).TotalSeconds < 8)
        {
            var last = probe.LastPayload;
            if (last.HasValue) { p = last.Value; break; }
            await Task.Delay(80);
        }
        Assert.True(p.HasValue, "expected at least one metrics payload within 8s");

        // 若包含 disk: 允许是 warming_up 占位或带数据对象
        if (p.Value.TryGetProperty("disk", out var d))
        {
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
            {
                var s = st.GetString() ?? string.Empty;
                Assert.True(string.Equals(s, "warming_up", StringComparison.OrdinalIgnoreCase) || s.Length > 0);
            }
        }
    }

    [Fact]
    public async Task SetConfig_Disk_Runtime_Config_Accepts()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(15));
        var helloParams = new { app_version = "1.0.0", protocol_version = 1, token = "t", capabilities = new[] { "metrics_stream" } };
        await rpc.InvokeAsync<object>("hello", new object[] { helloParams });

        // 提交较小 TTL，验证后端接受（后端会做范围夹紧）
        var cfg = new { disk_smart_native_enabled = true, disk_smart_ttl_ms = 7000, disk_nvme_errorlog_ttl_ms = 15000, disk_nvme_ident_ttl_ms = 120000 };
        var res = await rpc.InvokeAsync<JsonElement>("set_config", new object[] { cfg });
        Assert.True(res.TryGetProperty("ok", out var okEl) && okEl.GetBoolean());
    }

    [Fact]
    public async Task HighFrequency_Snapshot_Loop_Is_Stable_For_Short_Window()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(15));
        var helloParams = new { app_version = "1.0.0", protocol_version = 1, token = "t", capabilities = new[] { "metrics_stream" } };
        await rpc.InvokeAsync<object>("hello", new object[] { helloParams });

        var sw = Stopwatch.StartNew(); int ok = 0, err = 0;
        while (sw.ElapsedMilliseconds < 3000)
        {
            try
            {
                var snap = await rpc.InvokeAsync<JsonElement>("snapshot", new object[] { new { modules = new[] { "cpu", "memory", "disk" } } });
                Assert.True(snap.TryGetProperty("ts", out _));
                ok++;
            }
            catch
            {
                err++;
            }
            await Task.Delay(50);
        }
        // 允许偶发失败，但应大部分成功
        Assert.True(ok >= 20, $"expected >=20 successful snapshots, ok={ok}, err={err}");
    }
}
