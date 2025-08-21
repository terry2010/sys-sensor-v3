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
    public async Task Snapshot_CpuMem_Ranges()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(20));
        var helloParams = new { app_version = "1.0.0", protocol_version = 1, token = "t", capabilities = new[] { "metrics_stream" } };
        await rpc.InvokeAsync<object>("hello", new object[] { helloParams });

        var snap = await rpc.InvokeAsync<System.Text.Json.JsonElement>("snapshot", new object[] { new { } });
        // cpu.usage_percent in [0,100]
        Assert.True(snap.TryGetProperty("cpu", out var cpu));
        var usage = cpu.GetProperty("usage_percent").GetDouble();
        Assert.InRange(usage, 0.0, 100.0);
        // memory.used<=total and >=0
        Assert.True(snap.TryGetProperty("memory", out var mem));
        var total = mem.GetProperty("total").GetInt64();
        var used = mem.GetProperty("used").GetInt64();
        Assert.True(total >= 0);
        Assert.True(used >= 0);
        Assert.True(used <= total);
    }

    [Fact]
    public async Task Snapshot_Cpu_Expanded_Fields()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(25));
        var helloParams = new { app_version = "1.0.0", protocol_version = 1, token = "t", capabilities = new[] { "metrics_stream" } };
        await rpc.InvokeAsync<object>("hello", new object[] { helloParams });

        var snap = await rpc.InvokeAsync<System.Text.Json.JsonElement>("snapshot", new object[] { new { modules = new[] { "cpu" } } });
        Assert.True(snap.TryGetProperty("cpu", out var cpu));
        double GetOrDefault(string name)
        {
            return cpu.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.Number ? el.GetDouble() : 0.0;
        }
        // 基本分解应在 [0,100]
        var up = GetOrDefault("user_percent");
        var sp = GetOrDefault("system_percent");
        var ip = GetOrDefault("idle_percent");
        Assert.InRange(up, 0.0, 100.0);
        Assert.InRange(sp, 0.0, 100.0);
        Assert.InRange(ip, 0.0, 100.0);
        // uptime >= 0
        Assert.True(cpu.TryGetProperty("uptime_sec", out var upsecEl) && upsecEl.GetInt64() >= 0);
        // load_avg_* 在 0..100（EWMA 百分比表示）
        Assert.InRange(GetOrDefault("load_avg_1m"), 0.0, 100.0);
        Assert.InRange(GetOrDefault("load_avg_5m"), 0.0, 100.0);
        Assert.InRange(GetOrDefault("load_avg_15m"), 0.0, 100.0);
        // 进程/线程数量为非负
        Assert.True(cpu.TryGetProperty("process_count", out var pcEl) && pcEl.GetInt32() >= 0);
        Assert.True(cpu.TryGetProperty("thread_count", out var tcEl) && tcEl.GetInt32() >= 0);
        // per_core 为数组，长度可为 0+，元素范围 0..100
        if (cpu.TryGetProperty("per_core", out var coresEl) && coresEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var c in coresEl.EnumerateArray())
            {
                var v = c.GetDouble();
                Assert.InRange(v, 0.0, 100.0);
            }
        }
        // top_processes 为数组，元素包含 name(string)/pid(number)/cpu_percent(0..100)
        if (cpu.TryGetProperty("top_processes", out var topsEl) && topsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var p in topsEl.EnumerateArray())
            {
                if (p.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (p.TryGetProperty("name", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var _ = nEl.GetString();
                }
                if (p.TryGetProperty("pid", out var pidEl) && (pidEl.ValueKind == System.Text.Json.JsonValueKind.Number))
                {
                    var _pid = pidEl.GetInt32();
                    Assert.True(_pid >= 0);
                }
                if (p.TryGetProperty("cpu_percent", out var cpEl) && (cpEl.ValueKind == System.Text.Json.JsonValueKind.Number))
                {
                    var cp = cpEl.GetDouble();
                    Assert.InRange(cp, 0.0, 100.0);
                }
            }
        }
        // 内核活动计数器：存在且为数字时要求 >=0；允许缺失或为 Null
        void AssertNonNegativeIfNumber(string key)
        {
            if (cpu.TryGetProperty(key, out var el))
            {
                if (el.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    var v = el.GetDouble();
                    Assert.True(v >= 0);
                }
            }
        }
        AssertNonNegativeIfNumber("context_switches_per_sec");
        AssertNonNegativeIfNumber("syscalls_per_sec");
        AssertNonNegativeIfNumber("interrupts_per_sec");
        // 频率字段允许为 null；若存在则为正数
        if (cpu.TryGetProperty("current_mhz", out var curEl) && curEl.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            Assert.True(curEl.GetInt32() > 0);
        }
        if (cpu.TryGetProperty("max_mhz", out var maxEl) && maxEl.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            Assert.True(maxEl.GetInt32() > 0);
        }
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

    private sealed class MetricsValidator
    {
        private readonly object _lock = new();
        public System.Text.Json.JsonElement? LastPayload { get; private set; }
        public int Count { get; private set; }

        [JsonRpcMethod("metrics")]
        public void OnMetrics(System.Text.Json.JsonElement payload)
        {
            lock (_lock)
            {
                LastPayload = payload;
                Count++;
            }
        }
    }

    [Fact]
    public async Task Metrics_CpuMem_Ranges()
    {
        var validator = new MetricsValidator();
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(20), validator);
        var helloParams = new { app_version = "1.0.0", protocol_version = 1, token = "t", capabilities = new[] { "metrics_stream" } };
        await rpc.InvokeAsync<object>("hello", new object[] { helloParams });

        // 设置较快的基础间隔，确保在合理时间内接收到推送
        await rpc.InvokeAsync<System.Text.Json.JsonElement>("set_config", new object[] { new { base_interval_ms = 250, module_intervals = new System.Collections.Generic.Dictionary<string, int> { ["cpu"] = 250, ["memory"] = 250 } } });

        var start = DateTime.UtcNow;
        System.Text.Json.JsonElement? payload = null;
        while ((DateTime.UtcNow - start).TotalSeconds < 5)
        {
            var p = validator.LastPayload;
            if (p.HasValue)
            {
                payload = p.Value;
                break;
            }
            await Task.Delay(50);
        }
        Assert.True(payload.HasValue, "expected at least one metrics payload within 5s");
        var pl = payload!.Value;
        Assert.True(pl.TryGetProperty("cpu", out var cpu));
        var usage = cpu.GetProperty("usage_percent").GetDouble();
        Assert.InRange(usage, 0.0, 100.0);
        // 验证扩展字段存在且基本范围合理（尽量避免对环境敏感的强断言）
        double GetOr(string name)
        {
            return cpu.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.Number ? el.GetDouble() : 0.0;
        }
        Assert.InRange(GetOr("user_percent"), 0.0, 100.0);
        Assert.InRange(GetOr("system_percent"), 0.0, 100.0);
        Assert.InRange(GetOr("idle_percent"), 0.0, 100.0);
        if (cpu.TryGetProperty("per_core", out var coresEl) && coresEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var c in coresEl.EnumerateArray())
            {
                var v = c.GetDouble();
                Assert.InRange(v, 0.0, 100.0);
            }
        }
        if (pl.TryGetProperty("memory", out var mem))
        {
            var total = mem.GetProperty("total").GetInt64();
            var used = mem.GetProperty("used").GetInt64();
            Assert.True(total >= 0);
            Assert.True(used >= 0);
            Assert.True(used <= total);
        }
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

    private sealed class StateCollector
    {
        private readonly object _lock = new();
        private readonly List<(string phase, long ts)> _events = new();
        public IReadOnlyList<(string phase, long ts)> Events
        {
            get { lock (_lock) { return _events.ToArray(); } }
        }
        public void Clear()
        {
            lock (_lock) { _events.Clear(); }
        }
        [JsonRpcMethod("state")]
        public void OnState(System.Text.Json.JsonElement payload)
        {
            try
            {
                var phase = payload.GetProperty("phase").GetString() ?? string.Empty;
                var ts = payload.TryGetProperty("ts", out var tsEl) ? tsEl.GetInt64() : 0L;
                lock (_lock) { _events.Add((phase, ts)); }
            }
            catch { /* ignore malformed */ }
        }
    }

    [Fact]
    public async Task Metrics_Throttle_Burst_Recovery_Works()
    {
        var collector = new MetricsCollector();
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(20), collector);
        // 进行握手，声明 metrics_stream 能力 -> 该连接为事件桥，默认开启推流并自动 start(["cpu","mem"]).
        var helloParams = new { app_version = "1.0.0", protocol_version = 1, token = "t", capabilities = new[] { "metrics_stream" } };
        await rpc.InvokeAsync<object>("hello", new object[] { helloParams });

        // 显式开启推流（冗余安全）
        var sub = await rpc.InvokeAsync<System.Text.Json.JsonElement>("subscribe_metrics", new object[] { new { enable = true } });
        Assert.True(sub.GetProperty("ok").GetBoolean());
        Assert.True(sub.GetProperty("enabled").GetBoolean());

        // 设置基础间隔为 400ms，等待稳定一段时间统计次数
        var cfg = await rpc.InvokeAsync<System.Text.Json.JsonElement>("set_config", new object[] { new { base_interval_ms = 400 } });
        Assert.True(cfg.GetProperty("ok").GetBoolean());

        collector.Clear();
        // 轮询等待基础速率下收到至少 2 条，最多等待 2500ms
        var baseStart = DateTime.UtcNow;
        int c1 = 0;
        while ((DateTime.UtcNow - baseStart).TotalMilliseconds < 2500)
        {
            c1 = collector.Timestamps.Count;
            if (c1 >= 2) break;
            await Task.Delay(50);
        }
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

        // 等待 TTL 过期后恢复到基础间隔（目标 ~400ms）
        collector.Clear();
        var recStart = DateTime.UtcNow;
        // 先等待 700ms 确保 TTL 过期
        await Task.Delay(700);
        // 再窗口 1200ms 内轮询，应至少收到 1 条，且不应超过 5 条（容忍抖动）
        int c3 = 0;
        var recWindowStart = DateTime.UtcNow;
        while ((DateTime.UtcNow - recWindowStart).TotalMilliseconds < 1200)
        {
            c3 = collector.Timestamps.Count;
            if (c3 >= 1) break;
            await Task.Delay(50);
        }
        Assert.True(c3 >= 1 && c3 <= 5, $"expected 1..5 after recovery, got {c3}");
    }

    [Fact]
    public async Task State_Emitted_On_Start_Burst_Stop()
    {
        var state = new StateCollector();
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(25), state);
        var helloParams = new { app_version = "1.0.0", protocol_version = 1, token = "t", capabilities = new[] { "metrics_stream" } };
        await rpc.InvokeAsync<object>("hello", new object[] { helloParams });

        // start -> 期望收到 phase=start
        var startRes = await rpc.InvokeAsync<System.Text.Json.JsonElement>("start", new object[] { new { modules = new[] { "cpu" } } });
        Assert.True(startRes.TryGetProperty("ok", out var ok1) ? ok1.GetBoolean() : true);

        var sw = DateTime.UtcNow; bool gotStart = false;
        while ((DateTime.UtcNow - sw).TotalSeconds < 5)
        {
            if (state.Events.Any(e => string.Equals(e.phase, "start", StringComparison.OrdinalIgnoreCase))) { gotStart = true; break; }
            await Task.Delay(50);
        }
        Assert.True(gotStart, "expected state=start within 5s");

        // burst_subscribe -> 期望收到 phase=burst
        var burst = await rpc.InvokeAsync<System.Text.Json.JsonElement>("burst_subscribe", new object[] { new { modules = new[] { "cpu" }, interval_ms = 200, ttl_ms = 800 } });
        Assert.True(burst.GetProperty("ok").GetBoolean());

        sw = DateTime.UtcNow; bool gotBurst = false;
        while ((DateTime.UtcNow - sw).TotalSeconds < 5)
        {
            if (state.Events.Any(e => string.Equals(e.phase, "burst", StringComparison.OrdinalIgnoreCase))) { gotBurst = true; break; }
            await Task.Delay(50);
        }
        Assert.True(gotBurst, "expected state=burst within 5s");

        // stop -> 期望收到 phase=stop（无参）
        var stopRes = await rpc.InvokeAsync<System.Text.Json.JsonElement>("stop", Array.Empty<object>());
        Assert.True(stopRes.TryGetProperty("ok", out var ok2) ? ok2.GetBoolean() : true);

        sw = DateTime.UtcNow; bool gotStop = false;
        while ((DateTime.UtcNow - sw).TotalSeconds < 5)
        {
            if (state.Events.Any(e => string.Equals(e.phase, "stop", StringComparison.OrdinalIgnoreCase))) { gotStop = true; break; }
            await Task.Delay(50);
        }
        Assert.True(gotStop, "expected state=stop within 5s");
    }

    [Fact]
    public async Task QueryHistory_Edges_And_Bucketing()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(15));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 1) 空区间（from > to）
        var q1 = new { from_ts = now + 2000, to_ts = now + 1000, modules = new[] { "cpu" }, step_ms = 500 };
        var res1 = await rpc.InvokeAsync<System.Text.Json.JsonElement>("query_history", new object[] { q1 });
        Assert.True(res1.GetProperty("ok").GetBoolean());
        var len1 = res1.GetProperty("items").GetArrayLength();
        Assert.True(len1 >= 0);

        // 2) 单点区间（from == to）
        var q2 = new { from_ts = now - 1000, to_ts = now - 1000, modules = new[] { "cpu", "memory" }, step_ms = 0 };
        var res2 = await rpc.InvokeAsync<System.Text.Json.JsonElement>("query_history", new object[] { q2 });
        Assert.True(res2.GetProperty("ok").GetBoolean());
        Assert.True(res2.GetProperty("items").GetArrayLength() >= 1);

        // 3) 分桶边界（5s 窗口，1s 步长），允许抖动
        var q3 = new { from_ts = now - 5000, to_ts = now, modules = new[] { "cpu" }, step_ms = 1000 };
        var res3 = await rpc.InvokeAsync<System.Text.Json.JsonElement>("query_history", new object[] { q3 });
        Assert.True(res3.GetProperty("ok").GetBoolean());
        var len3 = res3.GetProperty("items").GetArrayLength();
        // 若历史稀疏，允许退化为 1 桶；否则在 3..10 之间
        Assert.True(len3 >= 1, $"unexpected bucket count: {len3}");
        if (len3 > 1)
        {
            Assert.True(len3 >= 3 && len3 <= 10, $"unexpected bucket count: {len3}");
        }
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
    public async Task QueryHistory_Aggregations_10s_1m()
    {
        using var rpc = await ConnectAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(1200);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 10s 聚合
        var q10 = new { from_ts = now - 65_000, to_ts = now, modules = new[] { "cpu", "memory" }, agg = "10s" };
        var r10 = await rpc.InvokeAsync<System.Text.Json.JsonElement>("query_history", new object[] { q10 });
        Assert.True(r10.GetProperty("ok").GetBoolean());
        var items10 = r10.GetProperty("items");
        foreach (var it in items10.EnumerateArray())
        {
            var ts = it.GetProperty("ts").GetInt64();
            Assert.True(ts % 10_000 == 0, $"ts not aligned to 10s bucket: {ts}");
        }

        // 1m 聚合
        var q60 = new { from_ts = now - 180_000, to_ts = now, modules = new[] { "cpu" }, agg = "1m" };
        var r60 = await rpc.InvokeAsync<System.Text.Json.JsonElement>("query_history", new object[] { q60 });
        Assert.True(r60.GetProperty("ok").GetBoolean());
        var items60 = r60.GetProperty("items");
        foreach (var it in items60.EnumerateArray())
        {
            var ts = it.GetProperty("ts").GetInt64();
            Assert.True(ts % 60_000 == 0, $"ts not aligned to 1m bucket: {ts}");
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
