using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace AgentGate.Tests;

public sealed class McpProtocolTests
{
    [Fact]
    public async Task AuthorizedClientCanInitializeListAndCallPing()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var factory = new McpTestApplication(token);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var initialize = await SendAsync(client, "initialize", new
        {
            protocolVersion = "2025-11-25",
            capabilities = new { },
            clientInfo = new { name = "agent-gate-tests", version = "1.0" }
        }, cancellation.Token);
        Assert.True(initialize.RootElement.GetProperty("result").TryGetProperty("serverInfo", out _));
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");

        using var list = await SendAsync(client, "tools/list", new { }, cancellation.Token);
        var tool = Assert.Single(list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());
        Assert.Equal("gate_ping", tool.GetProperty("name").GetString());
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());

        var before = DateTimeOffset.UtcNow;
        using var call = await SendAsync(client, "tools/call", new { name = "gate_ping", arguments = new { } }, cancellation.Token);
        var after = DateTimeOffset.UtcNow;
        var result = call.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        var data = result.GetProperty("structuredContent");
        Assert.Equal("pong", data.GetProperty("message").GetString());
        var serverTime = data.GetProperty("serverTime").GetDateTimeOffset();
        Assert.Equal(TimeSpan.Zero, serverTime.Offset);
        Assert.InRange(serverTime, before, after);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
        await connection.OpenAsync(cancellation.Token);
        await using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT Caller, Tool, State, DurationMilliseconds, SanitizedParameters FROM AuditCalls";
        await using var reader = await audit.ExecuteReaderAsync(cancellation.Token);
        Assert.True(await reader.ReadAsync(cancellation.Token));
        Assert.Equal("test-caller", reader.GetString(0));
        Assert.Equal("gate_ping", reader.GetString(1));
        Assert.Equal("Succeeded", reader.GetString(2));
        Assert.True(reader.GetDouble(3) >= 0);
        Assert.Equal("{}", reader.GetString(4));
        Assert.False(await reader.ReadAsync(cancellation.Token));
    }

    private static async Task<JsonDocument> SendAsync(HttpClient client, string method, object parameters, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params = parameters
        }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            body = body.Split('\n').Last(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        }
        var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("error", out _));
        return document;
    }
}
