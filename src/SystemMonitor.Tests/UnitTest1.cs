namespace SystemMonitor.Tests;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {

    }

    [Fact]
    public void SnakeCase_Serialization_ShouldUseSnakeCaseNames()
    {
        var obj = new { AppVersion = "1.0.0", ProtocolVersion = 1, Token = "abc" };
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
        };
        string json = System.Text.Json.JsonSerializer.Serialize(obj, opts);

        Assert.Contains("app_version", json);
        Assert.Contains("protocol_version", json);
        Assert.Contains("token", json);
    }
}