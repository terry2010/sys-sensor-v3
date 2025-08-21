using System.Text.Json;
using Xunit;

namespace SystemMonitor.Tests;

public class ContractTests
{
    private static readonly JsonSerializerOptions Snake = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public void Hello_Request_Serializes_To_SnakeCase()
    {
        var req = new { AppVersion = "1.0.0", ProtocolVersion = 1, Token = "t", Capabilities = new[] { "metrics_stream" } };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.Contains("app_version", json);
        Assert.Contains("protocol_version", json);
        Assert.Contains("token", json);
        Assert.Contains("capabilities", json);
    }

    [Fact]
    public void SetConfig_Request_Serializes_To_SnakeCase()
    {
        var req = new
        {
            BaseIntervalMs = 1000,
            ModuleIntervals = new System.Collections.Generic.Dictionary<string, int> { ["cpu"] = 300, ["memory"] = 1200 },
            Persist = true
        };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.Contains("base_interval_ms", json);
        Assert.Contains("module_intervals", json);
        Assert.Contains("persist", json);
    }

    [Fact]
    public void Hello_Request_Should_Not_Contain_PascalCase_Keys()
    {
        var req = new { AppVersion = "1.0.0", ProtocolVersion = 1, Token = "t" };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.DoesNotContain("AppVersion", json);
        Assert.DoesNotContain("ProtocolVersion", json);
        Assert.DoesNotContain("Token", json);
    }

    [Fact]
    public void SetConfig_Request_Should_Not_Contain_PascalCase_Keys()
    {
        var req = new { BaseIntervalMs = 500, ModuleIntervals = new System.Collections.Generic.Dictionary<string, int>(), Persist = false };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.DoesNotContain("BaseIntervalMs", json);
        Assert.DoesNotContain("ModuleIntervals", json);
        Assert.DoesNotContain("Persist", json);
    }

    [Fact]
    public void Start_Request_Serializes_To_SnakeCase()
    {
        var req = new { Modules = new[] { "cpu", "memory" } };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.Contains("modules", json);
        Assert.DoesNotContain("Modules", json);
    }

    [Fact]
    public void Stop_Request_Serializes_To_SnakeCase()
    {
        var req = new { };
        var json = JsonSerializer.Serialize(req, Snake);
        // 空对象，严格断言
        Assert.Equal("{}", json);
    }

    [Fact]
    public void BurstSubscribe_Request_Serializes_To_SnakeCase()
    {
        var req = new { Modules = new[] { "cpu", "net" }, IntervalMs = 1000, TtlMs = 5000 };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.Contains("modules", json);
        Assert.Contains("interval_ms", json);
        Assert.Contains("ttl_ms", json);
        Assert.DoesNotContain("IntervalMs", json);
        Assert.DoesNotContain("TtlMs", json);
    }

    [Fact]
    public void Snapshot_Request_Serializes_To_SnakeCase()
    {
        var req = new { Modules = new[] { "cpu" } };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.Contains("modules", json);
        Assert.DoesNotContain("Modules", json);
    }

    [Fact]
    public void QueryHistory_Request_Serializes_To_SnakeCase()
    {
        var req = new { FromTs = 1710000000, ToTs = 1710003600, Modules = new[] { "cpu", "memory" }, StepMs = 1000 };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.Contains("from_ts", json);
        Assert.Contains("to_ts", json);
        Assert.Contains("modules", json);
        Assert.Contains("step_ms", json);
        Assert.DoesNotContain("FromTs", json);
        Assert.DoesNotContain("ToTs", json);
    }

    [Fact]
    public void SubscribeMetrics_Request_Serializes_To_SnakeCase()
    {
        var req = new { Enable = true };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.Contains("enable", json);
        Assert.DoesNotContain("Enable", json);
    }

    [Fact(Skip = "非 M1：后续里程碑接口")]
    public void SetLogLevel_Request_Serializes_To_SnakeCase()
    {
        var req = new { Level = "warning" };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.Contains("level", json);
        Assert.DoesNotContain("Level", json);
    }

    [Fact(Skip = "非 M1：后续里程碑接口")]
    public void UpdateCheck_Request_Serializes_To_SnakeCase()
    {
        var req = new { };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.Equal("{}", json);
    }

    [Fact(Skip = "非 M1：后续里程碑接口")]
    public void UpdateApply_Request_Serializes_To_SnakeCase()
    {
        var req = new { Version = "1.1.0" };
        var json = JsonSerializer.Serialize(req, Snake);
        Assert.Contains("version", json);
        Assert.DoesNotContain("Version", json);
    }
}
