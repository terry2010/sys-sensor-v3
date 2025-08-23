using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Xunit;

namespace SystemMonitor.Tests;

public class SchemaTests
{
    private static JsonSchema HelloSchema => new JsonSchemaBuilder()
        .Type(SchemaValueType.Object)
        .Properties(
            ("app_version", new JsonSchemaBuilder().Type(SchemaValueType.String).MinLength(1)),
            ("protocol_version", new JsonSchemaBuilder().Type(SchemaValueType.Integer).Minimum(1)),
            ("token", new JsonSchemaBuilder().Type(SchemaValueType.String).MinLength(1)),
            ("capabilities", new JsonSchemaBuilder().Type(SchemaValueType.Array)
                .Items(new JsonSchemaBuilder().Type(SchemaValueType.String))
            )
        )
        .Required("app_version", "protocol_version", "token")
        .Build();

    private static JsonSchema SetConfigSchema => new JsonSchemaBuilder()
        .Type(SchemaValueType.Object)
        .Properties(
            ("base_interval_ms", new JsonSchemaBuilder().Type(SchemaValueType.Integer).Minimum(100)),
            ("module_intervals", new JsonSchemaBuilder().Type(SchemaValueType.Object)
                .AdditionalProperties(new JsonSchemaBuilder().Type(SchemaValueType.Integer).Minimum(100))
            ),
            ("persist", new JsonSchemaBuilder().Type(SchemaValueType.Boolean))
        )
        .Build();

    private static JsonSchema BurstSubscribeSchema => new JsonSchemaBuilder()
        .Type(SchemaValueType.Object)
        .Properties(
            ("modules", new JsonSchemaBuilder().Type(SchemaValueType.Array)
                .MinItems(1)
                .Items(new JsonSchemaBuilder().Type(SchemaValueType.String).MinLength(1))
            ),
            ("interval_ms", new JsonSchemaBuilder().Type(SchemaValueType.Integer).Minimum(200)),
            ("ttl_ms", new JsonSchemaBuilder().Type(SchemaValueType.Integer).Minimum(1000))
        )
        .Required("modules", "interval_ms", "ttl_ms")
        .Build();

    private static JsonSchema SnapshotSchema => new JsonSchemaBuilder()
        .Type(SchemaValueType.Object)
        .Properties(
            ("modules", new JsonSchemaBuilder().Type(SchemaValueType.Array)
                .MinItems(1)
                .Items(new JsonSchemaBuilder().Type(SchemaValueType.String).MinLength(1))
            )
        )
        .Build();

    private static JsonSchema QueryHistorySchema => new JsonSchemaBuilder()
        .Type(SchemaValueType.Object)
        .Properties(
            ("start_ts", new JsonSchemaBuilder().Type(SchemaValueType.Integer).Minimum(0)),
            ("end_ts", new JsonSchemaBuilder().Type(SchemaValueType.Integer).Minimum(0)),
            ("modules", new JsonSchemaBuilder().Type(SchemaValueType.Array)
                .MinItems(1)
                .Items(new JsonSchemaBuilder().Type(SchemaValueType.String).MinLength(1))
            ),
            ("granularity", new JsonSchemaBuilder().Type(SchemaValueType.String)
                .Enum("1s", "10s", "1m")
            )
        )
        .Required("start_ts", "end_ts")
        .Build();

    private static bool IsValid(JsonSchema schema, object obj)
    {
        var json = JsonSerializer.SerializeToNode(obj, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;
        var result = schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        return result.IsValid;
    }

    private static JsonSchema TopProcessesByDiskSchema => new JsonSchemaBuilder()
        .Type(SchemaValueType.Object)
        .Properties(
            ("top_processes_by_disk", new JsonSchemaBuilder().Type(SchemaValueType.Array)
                .Items(new JsonSchemaBuilder().Type(SchemaValueType.Object)
                    .Properties(
                        ("pid", new JsonSchemaBuilder().Type(SchemaValueType.Integer).Minimum(0)),
                        ("name", new JsonSchemaBuilder().Type(SchemaValueType.String)),
                        ("read_bytes_per_sec", new JsonSchemaBuilder().Type(SchemaValueType.Integer).Minimum(0)),
                        ("write_bytes_per_sec", new JsonSchemaBuilder().Type(SchemaValueType.Integer).Minimum(0))
                    )
                    .Required("pid", "name", "read_bytes_per_sec", "write_bytes_per_sec")
                )
            )
        )
        .Required("top_processes_by_disk")
        .Build();

    [Fact]
    public void Hello_Schema_Valid_Minimal()
    {
        var req = new { AppVersion = "1.0.0", ProtocolVersion = 1, Token = "t" };
        Assert.True(IsValid(HelloSchema, req));
    }

    [Fact]
    public void Hello_Schema_Invalid_NoToken()
    {
        var req = new { AppVersion = "1.0.0", ProtocolVersion = 1 };
        Assert.False(IsValid(HelloSchema, req));
    }

    [Fact]
    public void SetConfig_Schema_Valid_WithIntervals()
    {
        var req = new { BaseIntervalMs = 1000, ModuleIntervals = new System.Collections.Generic.Dictionary<string, int> { ["cpu"] = 300 }, Persist = true };
        Assert.True(IsValid(SetConfigSchema, req));
    }

    [Fact]
    public void SetConfig_Schema_Invalid_TooSmallInterval()
    {
        var req = new { BaseIntervalMs = 50 };
        Assert.False(IsValid(SetConfigSchema, req));
    }

    [Fact]
    public void BurstSubscribe_Schema_Valid()
    {
        var req = new { Modules = new[] { "cpu", "net" }, IntervalMs = 1000, TtlMs = 10000 };
        Assert.True(IsValid(BurstSubscribeSchema, req));
    }

    [Fact]
    public void BurstSubscribe_Schema_Invalid_EmptyModules()
    {
        var req = new { Modules = System.Array.Empty<string>(), IntervalMs = 1000, TtlMs = 10000 };
        Assert.False(IsValid(BurstSubscribeSchema, req));
    }

    [Fact]
    public void Snapshot_Schema_Valid_WithModules()
    {
        var req = new { Modules = new[] { "cpu" } };
        Assert.True(IsValid(SnapshotSchema, req));
    }

    [Fact]
    public void QueryHistory_Schema_Valid()
    {
        var req = new { StartTs = 1710000000, EndTs = 1710003600, Modules = new[] { "cpu" }, Granularity = "10s" };
        Assert.True(IsValid(QueryHistorySchema, req));
    }

    [Fact]
    public void QueryHistory_Schema_Invalid_Granularity()
    {
        var req = new { StartTs = 1, EndTs = 2, Granularity = "5s" };
        Assert.False(IsValid(QueryHistorySchema, req));
    }

    [Fact]
    public void Disk_TopProcesses_Schema_Valid()
    {
        var resp = new
        {
            TopProcessesByDisk = new[]
            {
                new { Pid = 100, Name = "a", ReadBytesPerSec = 10L, WriteBytesPerSec = 20L },
                new { Pid = 200, Name = "b", ReadBytesPerSec = 0L, WriteBytesPerSec = 0L }
            }
        };
        Assert.True(IsValid(TopProcessesByDiskSchema, resp));
    }

    [Fact]
    public void Disk_TopProcesses_Schema_Invalid_NegativePid()
    {
        var resp = new
        {
            TopProcessesByDisk = new[]
            {
                new { Pid = -1, Name = "x", ReadBytesPerSec = 1L, WriteBytesPerSec = 1L }
            }
        };
        Assert.False(IsValid(TopProcessesByDiskSchema, resp));
    }

    [Fact]
    public void Disk_TopProcesses_Schema_Invalid_MissingName()
    {
        var resp = new
        {
            TopProcessesByDisk = new object[]
            {
                new System.Collections.Generic.Dictionary<string, object>
                {
                    ["Pid"] = 1,
                    ["ReadBytesPerSec"] = 1L,
                    ["WriteBytesPerSec"] = 2L
                }
            }
        };
        Assert.False(IsValid(TopProcessesByDiskSchema, resp));
    }
}
