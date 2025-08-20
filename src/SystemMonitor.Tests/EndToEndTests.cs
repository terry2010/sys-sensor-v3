using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;
using System.Collections.Generic;

namespace SystemMonitor.Tests;

[SupportedOSPlatform("windows")]
public class EndToEndTests : IAsyncLifetime
{
    private Process? _svc;
    private const string PipeName = "sys_sensor_v3.rpc";
    private DateTime _svcStartAt;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _svcLog = new();
    private const int MaxSvcLogLines = 200;
    private string? _svcWorkDir;

    private string ReadSerilogTail(int maxLines = 200)
    {
        try
        {
            // 优先从服务进程工作目录查找 logs/；否则回退到仓库根目录 logs/
            string? logsDir = null;
            if (!string.IsNullOrEmpty(_svcWorkDir))
            {
                var candidate = Path.Combine(_svcWorkDir!, "logs");
                if (Directory.Exists(candidate)) logsDir = candidate;
            }
            if (logsDir == null)
            {
                var root = FindRepoRoot();
                var candidate = Path.Combine(root, "logs");
                if (Directory.Exists(candidate)) logsDir = candidate;
            }
            if (logsDir == null) return string.Empty;
            var files = new DirectoryInfo(logsDir).GetFiles("service-*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            if (files.Length == 0) return string.Empty;
            var file = files[0].FullName;
            var lines = File.ReadLines(file);
            var buf = new System.Collections.Generic.Queue<string>(maxLines);
            foreach (var l in lines)
            {
                if (buf.Count == maxLines) buf.Dequeue();
                buf.Enqueue(l);
            }
            return string.Join(Environment.NewLine, buf);
        }
        catch
        {
            return string.Empty;
        }
    }

    // Win32 API: WaitNamedPipeW，用于等待命名管道可用而不实际连接
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WaitNamedPipe(string lpNamedPipeName, uint nTimeOut);

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_SEM_TIMEOUT = 121;
    private const int ERROR_PIPE_BUSY = 231;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            // 以解决方案文件或项目文件为锚点
            var sln = Path.Combine(dir.FullName, "SysSensorV3.sln");
            var csproj = Path.Combine(dir.FullName, "src", "SystemMonitor.Service", "SystemMonitor.Service.csproj");
            if (File.Exists(sln) || File.Exists(csproj))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repo root not found by anchor files");
    }

    private static string? FindServiceBinary()
    {
        // 默认使用 dotnet run 以确保使用最新源码构建。
        // 如需使用现成 Release 二进制，可设置环境变量 E2E_USE_BIN=1。
        var useBin = string.Equals(Environment.GetEnvironmentVariable("E2E_USE_BIN"), "1", StringComparison.Ordinal);
        if (!useBin) return null;

        var root = FindRepoRoot();
        var exe = Path.Combine(root, "src", "SystemMonitor.Service", "bin", "Release", "net8.0", "SystemMonitor.Service.exe");
        if (File.Exists(exe)) return exe;
        var dll = Path.Combine(root, "src", "SystemMonitor.Service", "bin", "Release", "net8.0", "SystemMonitor.Service.dll");
        if (File.Exists(dll)) return dll; // 将用 dotnet 运行
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
                if (localTarget != null)
                {
                    rpc.AddLocalRpcTarget(localTarget);
                }
                rpc.StartListening();
                return rpc;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(300);
            }
        }
        throw new TimeoutException($"Failed to connect named pipe within {timeout}. Last error: {last}");
    }

    private async Task WaitPipeReadyAsync(TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        Exception? last = null;
        var pipePath = @"\\.\pipe\" + PipeName;
        while (DateTime.UtcNow - start < timeout)
        {
            // 若服务进程已退出，立即失败并附带尾部日志
            try
            {
                if (_svc != null && _svc.HasExited)
                {
                    var tail = string.Join(Environment.NewLine, _svcLog.ToArray());
                    var fileTail = ReadSerilogTail();
                    throw new InvalidOperationException($"Service exited while waiting for pipe. Code={_svc.ExitCode}. Tail:\n{tail}\n[file-log-tail]\n{fileTail}");
                }
            }
            catch (InvalidOperationException) { throw; }
            catch { /* ignore */ }
            // 使用 Win32 WaitNamedPipe 等待可用，而不消耗服务端的单实例连接
            if (WaitNamedPipe(pipePath, 500))
            {
                return;
            }
            var err = Marshal.GetLastWin32Error();
            // 常见错误：
            // ERROR_FILE_NOT_FOUND (2): 管道尚未创建；ERROR_PIPE_BUSY (231): 管道正忙；ERROR_SEM_TIMEOUT (121): 等待超时
            last = new Win32Exception(err);
            try
            {
                // 将最近一次错误打印到测试输出，便于诊断（不频繁刷屏）
                Console.WriteLine($"[test][pipe-wait] last-win32={err} elapsed={(DateTime.UtcNow-start).TotalSeconds:F1}s");
            }
            catch { /* ignore */ }
            await Task.Delay(200).ConfigureAwait(false);
        }
        var tail2 = string.Join(Environment.NewLine, _svcLog.ToArray());
        var fileTail2 = ReadSerilogTail();
        throw new TimeoutException($"Service pipe not ready within {timeout}. Last error: {last}. Tail:\n{tail2}\n[file-log-tail]\n{fileTail2}");
    }

    public async Task InitializeAsync()
    {
        // 先清理可能残留的同名服务进程，避免占用命名管道（max instances=1）
        try
        {
            foreach (var p in Process.GetProcessesByName("SystemMonitor.Service"))
            {
                try
                {
                    // 仅终止当前用户会话下的同名进程（简单防御：忽略非本会话或权限不足）
                    p.Kill(true);
                    p.WaitForExit(2000);
                }
                catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
        // 优先使用已存在的 Release 产物；若不存在则以 dotnet run 启动项目
        var bin = FindServiceBinary();
        // 计算仓库根目录（用于统一内容根路径）
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
                // 使用二进制所在目录作为内容根，确保 appsettings 等配置就位
                WorkingDirectory = binDir,
            };
            _svcWorkDir = binDir;
        }
        else
        {
            // dotnet run --project src/SystemMonitor.Service -c Release
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
            _svcWorkDir = root;
        }

        _svc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start service process");
        // 异步读取输出以避免缓冲区阻塞
        try
        {
            void Enqueue(string line)
            {
                _svcLog.Enqueue(line);
                while (_svcLog.Count > MaxSvcLogLines && _svcLog.TryDequeue(out _)) { }
            }
            _svc.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    var line = $"[svc][out] {e.Data}";
                    Console.WriteLine(line);
                    Enqueue(line);
                }
            };
            _svc.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    var line = $"[svc][err] {e.Data}";
                    Console.WriteLine(line);
                    Enqueue(line);
                }
            };
            _svc.BeginOutputReadLine();
            _svc.BeginErrorReadLine();
        }
        catch { /* 某些平台/环境下可能不支持，忽略 */ }
        _svcStartAt = DateTime.UtcNow;
        // 若进程异常快速退出，立即失败并输出退出码，避免长时间等待管道
        await Task.Delay(500);
        if (_svc.HasExited)
        {
            var tail = string.Join(Environment.NewLine, _svcLog.ToArray());
            throw new InvalidOperationException($"Service process exited early with code {_svc.ExitCode}. Please check service logs. Tail:\n{tail}");
        }
        // 主动探测命名管道是否就绪：
        // 无二进制 -> dotnet run 首次构建可能较慢，放宽到 120 秒；
        // 有二进制 -> 45 秒足够完成启动。
        var waitTimeout = (bin == null) ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(45);
        await WaitPipeReadyAsync(waitTimeout);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (_svc != null && !_svc.HasExited)
            {
                _svc.Kill(true);
                _svc.WaitForExit(2000);
            }
        }
        catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Hello_Then_Snapshot_Works()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(30));

        var helloParams = new
        {
            app_version = "1.0.0",
            protocol_version = 1,
            token = "token",
            capabilities = new[] { "metrics_stream" }
        };
        var hello = await rpc.InvokeAsync<System.Text.Json.JsonElement>("hello", new object[] { helloParams });
        Assert.True(hello.TryGetProperty("server_version", out var _));
        Assert.Equal(1, hello.GetProperty("protocol_version").GetInt32());

        // 日志应包含 hello 调用
        await WaitLogContainsAsync("hello:", TimeSpan.FromSeconds(5));

        var snap = await rpc.InvokeAsync<System.Text.Json.JsonElement>("snapshot", new object[] { new { } });
        Assert.True(snap.TryGetProperty("ts", out var _));
    }

    [Fact]
    public async Task SetConfig_And_BurstSubscribe_Works()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(10));

        var cfg = await rpc.InvokeAsync<System.Text.Json.JsonElement>("set_config", new object[] { new { base_interval_ms = 200 } });
        Assert.True(cfg.GetProperty("ok").GetBoolean());

        var burst = await rpc.InvokeAsync<System.Text.Json.JsonElement>("burst_subscribe", new object[] { new { modules = new[] { "cpu" }, interval_ms = 200, ttl_ms = 1500 } });
        Assert.True(burst.GetProperty("ok").GetBoolean());
        Assert.True(burst.GetProperty("expires_at").GetInt64() > 0);
    }

    private sealed class MetricsCollector
    {
        private readonly object _lock = new();
        private readonly List<long> _ts = new();
        public IReadOnlyList<long> Timestamps
        {
            get { lock (_lock) { return _ts.ToArray(); } }
        }
        public void Clear()
        {
            lock (_lock) { _ts.Clear(); }
        }
        [JsonRpcMethod("metrics")]
        public void OnMetrics(System.Text.Json.JsonElement payload)
        {
            try
            {
                var ts = payload.GetProperty("ts").GetInt64();
                lock (_lock) { _ts.Add(ts); }
            }
            catch { /* ignore malformed */ }
        }
    }

    [Fact]
    public async Task Metrics_Throttle_Burst_Recovery_Works()
    {
        var collector = new MetricsCollector();
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(20), collector);

        // 设置基础间隔为 400ms，等待稳定一段时间统计次数
        var cfg = await rpc.InvokeAsync<System.Text.Json.JsonElement>("set_config", new object[] { new { base_interval_ms = 400 } });
        Assert.True(cfg.GetProperty("ok").GetBoolean());

        collector.Clear();
        await Task.Delay(1300); // 约 3 个周期
        var c1 = collector.Timestamps.Count;
        Assert.True(c1 >= 2, $"expected >=2 at base rate, got {c1}");

        // 进入 burst: 100ms，TTL 600ms
        var burst = await rpc.InvokeAsync<System.Text.Json.JsonElement>("burst_subscribe", new object[] { new { modules = new[] { "cpu" }, interval_ms = 100, ttl_ms = 600 } });
        Assert.True(burst.GetProperty("ok").GetBoolean());

        collector.Clear();
        // 轮询等待，最多 1200ms，直到收集到 >=4 条（避免调度抖动造成偶发失败）
        var swStart = DateTime.UtcNow;
        int c2 = 0;
        while ((DateTime.UtcNow - swStart).TotalMilliseconds < 1200)
        {
            c2 = collector.Timestamps.Count;
            if (c2 >= 4) break;
            await Task.Delay(50);
        }
        Assert.True(c2 >= 3, $"expected >=3 during burst (target >=4), got {c2}");

        // 等待 TTL 过期后恢复到基础间隔
        collector.Clear();
        await Task.Delay(900); // 超过 TTL，恢复 base（~400ms）
        var c3 = collector.Timestamps.Count;
        Assert.True(c3 >= 1 && c3 <= 4, $"expected 1..4 after recovery, got {c3}");
    }

    [Fact]
    public async Task QueryHistory_Works()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(10));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var q = new { from_ts = now - 5000, to_ts = now, modules = new[] { "cpu", "memory" }, step_ms = 1000 };
        var res = await rpc.InvokeAsync<System.Text.Json.JsonElement>("query_history", new object[] { q });
        Assert.True(res.GetProperty("ok").GetBoolean());
        Assert.True(res.GetProperty("items").GetArrayLength() > 0);
        foreach (var item in res.GetProperty("items").EnumerateArray())
        {
            Assert.True(item.TryGetProperty("ts", out _));
            Assert.True(item.TryGetProperty("cpu", out _));
            Assert.True(item.TryGetProperty("memory", out _));
        }
    }

    [Fact]
    public async Task Hello_WithoutToken_Fails()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<RemoteInvocationException>(async () =>
        {
            var bad = new { app_version = "1.0.0", protocol_version = 1, token = "" };
            await rpc.InvokeAsync<object>("hello", new object[] { bad });
        });
    }

    [Fact]
    public async Task Hello_Protocol_NotSupported_Fails()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<RemoteInvocationException>(async () =>
        {
            var bad = new { app_version = "1.0.0", protocol_version = 2, token = "t" };
            await rpc.InvokeAsync<object>("hello", new object[] { bad });
        });
    }

    [Fact]
    public async Task Hello_Unsupported_Capability_Fails()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<RemoteInvocationException>(async () =>
        {
            var bad = new { app_version = "1.0.0", protocol_version = 1, token = "t", capabilities = new[] { "unknown_cap" } };
            await rpc.InvokeAsync<object>("hello", new object[] { bad });
        });
    }

    [Fact]
    public async Task SetConfig_Invalid_BaseInterval_Fails()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<RemoteInvocationException>(async () =>
        {
            await rpc.InvokeAsync<object>("set_config", new object[] { new { base_interval_ms = 0 } });
        });
    }

    private static string? GetLogsDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir?.Parent != null; i++) dir = dir!.Parent;
        return dir == null ? null : Path.Combine(dir.FullName, "logs");
    }

    private async Task WaitLogContainsAsync(string keyword, TimeSpan timeout)
    {
        var logs = GetLogsDir();
        if (logs == null) return; // 忽略日志断言
        var start = DateTime.UtcNow;
        Exception? last = null;
        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                if (!Directory.Exists(logs)) { await Task.Delay(200); continue; }
                var files = Directory.GetFiles(logs, "*.log");
                if (files.Length == 0) { await Task.Delay(200); continue; }
                var recent = files
                    .Select(p => new FileInfo(p))
                    .Where(f => f.LastWriteTimeUtc >= _svcStartAt.AddSeconds(-2))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(3)
                    .ToArray();
                foreach (var f in recent)
                {
                    var text = await File.ReadAllTextAsync(f.FullName);
                    if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return;
                }
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(200);
            }
        }
        if (last != null) throw new TimeoutException($"Log wait failed: {last.Message}");
    }
}
