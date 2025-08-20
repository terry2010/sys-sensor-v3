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
}
