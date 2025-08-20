using System.IO.Pipes;
using System.Collections.Generic;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Nerdbank.Streams;
using StreamJsonRpc;

internal class Program
{
    private const string PipeName = @"sys_sensor_v3.rpc";
    private static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            // 统一控制台为 UTF-8，避免中文日志乱码
            Console.OutputEncoding = Encoding.UTF8;
            // 解析 --log 参数（或 -l），将输出镜像到 UTF-8(BOM) 文件
            string? logPath = null;
            int? optBaseInterval = null;
            Dictionary<string, int>? optModuleIntervals = null;
            for (int i = 0; i < args.Length; i++)
            {
                if ((string.Equals(args[i], "--log", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(args[i], "-l", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    logPath = args[i + 1];
                    break;
                }
            }

            // 额外循环解析 --base-interval（不与 --log 冲突）
            for (int i = 0; i < args.Length; i++)
            {
                if ((string.Equals(args[i], "--base-interval", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(args[i], "-b", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out var bi) && bi > 0)
                    {
                        optBaseInterval = bi;
                    }
                }
                if ((string.Equals(args[i], "--module-intervals", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(args[i], "-m", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    var spec = args[i + 1];
                    var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
                        if (kv.Length == 2 && int.TryParse(kv[1], out var ms) && ms > 0)
                        {
                            var key = kv[0];
                            if (!string.IsNullOrWhiteSpace(key)) dict[key] = ms;
                        }
                    }
                    if (dict.Count > 0) optModuleIntervals = dict;
                }
            }

            TextWriter? fileWriter = null;
            TextWriter? originalOut = Console.Out;
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    // UTF-8 带 BOM
                    fileWriter = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)) { AutoFlush = true };
                    Console.SetOut(new TeeTextWriter(originalOut, fileWriter));
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"无法写入日志文件 {logPath}: {ex.Message}");
                    Console.ResetColor();
                }
            }
            var tokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "sys-sensor-v3", "token");

            if (!File.Exists(tokenPath))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"未找到开发 token 文件: {tokenPath}");
                Console.WriteLine("请先创建该文件并填写 token，再运行本客户端。");
                Console.ResetColor();
                return 2;
            }

            var token = (await File.ReadAllTextAsync(tokenPath, Encoding.UTF8).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.WriteLine("token 文件为空。");
                return 2;
            }

            using var client = new NamedPipeClientStream(
                serverName: ".",
                pipeName: PipeName,
                direction: PipeDirection.InOut,
                options: PipeOptions.Asynchronous);

            Console.WriteLine($"连接到命名管道: {PipeName} …（最多等待30秒）");
            var start = DateTimeOffset.UtcNow;
            Exception? lastConnectError = null;
            while (true)
            {
                try
                {
                    await client.ConnectAsync(1000).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    lastConnectError = ex;
                    if ((DateTimeOffset.UtcNow - start).TotalSeconds >= 30)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("连接超时，请确认服务端已启动并在监听命名管道。");
                        Console.WriteLine(ex.ToString());
                        Console.ResetColor();
                        return 3;
                    }
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
            Console.WriteLine("已连接。");

            var reader = client.UsePipeReader();
            var writer = client.UsePipeWriter();
            var formatter = new SystemTextJsonFormatter();
            formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;

            var handler = new HeaderDelimitedMessageHandler(writer, reader, formatter);
            using var rpc = new JsonRpc(handler);
            var metricsSink = new MetricsSink();
            rpc.AddLocalRpcTarget(metricsSink);
            rpc.StartListening();

            // 调用 hello({ app_version, protocol_version, token, capabilities })
            Console.WriteLine("调用 hello() …");
            var helloParams = new
            {
                appVersion = "client-smoke-0.1",
                protocolVersion = 1,
                token = token,
                capabilities = new[] { "smoke_test" }
            };
            var hello = await rpc.InvokeAsync<JsonElement>("hello", new object?[] { helloParams }).ConfigureAwait(false);
            Console.WriteLine("hello 返回:");
            Console.WriteLine(JsonSerializer.Serialize(hello, SnakeCase));

            // 调用 snapshot({})
            Console.WriteLine("调用 snapshot() …");
            var snapshot = await rpc.InvokeAsync<JsonElement>("snapshot", new object?[] { new { } }).ConfigureAwait(false);
            Console.WriteLine("snapshot 返回:");
            Console.WriteLine(JsonSerializer.Serialize(snapshot, SnakeCase));

            // 若指定了基础间隔或模块间隔，调用 set_config
            if (optBaseInterval.HasValue || (optModuleIntervals != null && optModuleIntervals.Count > 0))
            {
                if (optBaseInterval.HasValue)
                {
                    Console.WriteLine($"调用 set_config(base_interval_ms={optBaseInterval.Value}{(optModuleIntervals!=null?", module_intervals=...":"")}) …");
                }
                else
                {
                    Console.WriteLine("调用 set_config(module_intervals=...) …");
                }
                var scParams = new { base_interval_ms = optBaseInterval, module_intervals = optModuleIntervals };
                Console.WriteLine("set_config 参数:");
                Console.WriteLine(JsonSerializer.Serialize(scParams, SnakeCase));
                var sc = await rpc.InvokeAsync<JsonElement>("set_config", new object?[] { scParams }).ConfigureAwait(false);
                Console.WriteLine("set_config 返回:");
                Console.WriteLine(JsonSerializer.Serialize(sc, SnakeCase));
            }

            // 触发 3 秒的高频推送（200ms 间隔）
            Console.WriteLine("调用 burst_subscribe(interval_ms=200, ttl_ms=3000) …");
            var burstParams = new { modules = new[] { "cpu", "memory" }, interval_ms = 200, ttl_ms = 3000 };
            var burst = await rpc.InvokeAsync<JsonElement>("burst_subscribe", new object?[] { burstParams }).ConfigureAwait(false);
            Console.WriteLine("burst_subscribe 返回:");
            Console.WriteLine(JsonSerializer.Serialize(burst, SnakeCase));

            // 等待一段时间接收 metrics 通知
            await Task.Delay(3500).ConfigureAwait(false);

            Console.WriteLine($"收到 metrics 通知条数: {metricsSink.Count}");
            if (metricsSink.LastJson is not null)
            {
                Console.WriteLine("最后一条 metrics:");
                Console.WriteLine(metricsSink.LastJson);
            }

            // 调用 query_history（最近3秒，cpu/memory）
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var qhParams = new { from_ts = nowMs - 3000, to_ts = nowMs, modules = new[] { "cpu", "memory" }, step_ms = (int?)null };
            Console.WriteLine("调用 query_history(最近3秒) …");
            var history = await rpc.InvokeAsync<JsonElement>("query_history", new object?[] { qhParams }).ConfigureAwait(false);
            Console.WriteLine("query_history 返回:");
            Console.WriteLine(JsonSerializer.Serialize(history, SnakeCase));

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("调用失败: " + ex);
            Console.ResetColor();
            return 1;
        }
    }

    // 本地通知接收器：处理服务端的 metrics 通知
    private class MetricsSink
    {
        private int _count = 0;
        public int Count => _count;
        public string? LastJson { get; private set; }

        [JsonRpcMethod("metrics")]
        public void OnMetrics(JsonElement payload)
        {
            Interlocked.Increment(ref _count);
            try
            {
                LastJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    WriteIndented = true,
                });
            }
            catch
            {
                // 忽略序列化异常，仅计数
            }
        }
    }

    // 同时写入控制台与文件的 Tee Writer
    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _a;
        private readonly TextWriter _b;
        public override Encoding Encoding => Encoding.UTF8;
        public TeeTextWriter(TextWriter a, TextWriter b)
        {
            _a = a; _b = b;
        }
        public override void Write(char value)
        {
            _a.Write(value);
            _b.Write(value);
        }
        public override void Write(string? value)
        {
            _a.Write(value);
            _b.Write(value);
        }
        public override void WriteLine(string? value)
        {
            _a.WriteLine(value);
            _b.WriteLine(value);
        }
        public override void Flush()
        {
            _a.Flush();
            _b.Flush();
        }
    }
}
