using System.Text;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Secrets;
using Microsoft.Extensions.Options;
using Xunit;
using static AgentGate.Tests.HomeAssistantLogsTests;

namespace AgentGate.Tests;

public sealed class HomeAssistantSystemLogTests
{
    [Fact]
    public async Task FragmentedUtf8ResultReassemblesBeforeJsonAndRedaction()
    {
        await using var fixture = new Fixture(Entry(1, "ő😀", level: "CRITICAL"));
        fixture.Sockets.FragmentSize = 3;
        using var call = await fixture.Call(new { level = "CRITICAL" });
        var data = call.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Contains("ő😀", data.GetProperty("items")[0].GetProperty("messages")[0].GetString());
        Assert.Equal(0, data.GetProperty("withheld_count").GetInt32());
    }

    [Fact]
    public async Task LoggerIsGuardedAndMetadataOnlyFallbackKeepsWithheldCount()
    {
        await using var fixture = new Fixture(Entry(1, logger: new string('a', 24_000)) + Entry(2, logger: "password=hidden"));
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        var data = result.GetProperty("structuredContent");
        Assert.Equal(0, data.GetProperty("returned_count").GetInt32());
        Assert.Equal(1, data.GetProperty("withheld_count").GetInt32());
        Assert.True(data.GetProperty("redacted").GetBoolean());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.InRange(Encoding.UTF8.GetByteCount(result.GetRawText()), 1, 32_000);
        await fixture.AssertNoRawData("hidden");
    }

    [Fact]
    public async Task SharedRedactorStillMasksUnambiguousCompositeAssignment()
    {
        await using var fixture = new Fixture(Entry(1, "passwordHash=hidden"));
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        Assert.False(result.ToString().Contains("hidden", StringComparison.Ordinal), "A közös titokszűrő nem futott le.");
        var data = result.GetProperty("structuredContent");
        Assert.Equal(1, data.GetProperty("returned_count").GetInt32());
        Assert.Equal(0, data.GetProperty("withheld_count").GetInt32());
        Assert.True(data.GetProperty("redacted").GetBoolean());
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task RedactorAssignmentSpanningMessagesMustNotReleaseUnredactedFields()
    {
        await using var fixture = new Fixture(Entry(1, messages: ["passwordHash=\"", "hidden\""]) + Entry(2));
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        Assert.False(result.ToString().Contains("hidden", StringComparison.Ordinal), "Mezőhatáron átnyúló titok maradt.");
        Assert.Equal(1, result.GetProperty("structuredContent").GetProperty("withheld_count").GetInt32());
    }

    [Theory]
    [InlineData("api_key=")]
    [InlineData("key=")]
    [InlineData("token=\u2028")]
    [InlineData("Bearer ")]
    [InlineData("password=\n")]
    [InlineData("client_secret=\n")]
    public async Task SensitiveEntryDoesNotRemoveSafeNeighbours(string marker)
    {
        await using var fixture = new Fixture(Entry(1, "keyboard tokenizer") + Entry(2, marker + "hidden") +
            Entry(3, exception: "KeyError: keyboard"));
        using var call = await fixture.Call(new { });
        var data = call.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(2, data.GetProperty("returned_count").GetInt32());
        Assert.Equal(1, data.GetProperty("withheld_count").GetInt32());
        Assert.True(data.GetProperty("redacted").GetBoolean());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        await fixture.AssertNoRawData("hidden");
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("exception")]
    [InlineData("logger")]
    [InlineData("mixed")]
    public async Task KnownTokenIsProtectedAcrossDecodedFieldsBeforeClipping(string split)
    {
        await using var fixture = new Fixture("");
        var token = fixture.Secrets.Token;
        var first = token[..32];
        var second = token[32..];
        fixture.Body = Entry(0) + (split switch
        {
            "logger" => Entry(1, logger: first, messages: [second]),
            "messages" => Entry(1, messages: [first + "\u2028", "\u200b" + second]),
            "exception" => Entry(1, messages: [first], exception: "\n\u2003" + second),
            _ => Entry(1, messages: [token, first + "\n"], exception: second)
        });
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        var data = result.GetProperty("structuredContent");
        Assert.Equal(1, data.GetProperty("withheld_count").GetInt32());
        Assert.Equal(1, data.GetProperty("returned_count").GetInt32());
        Assert.False(result.ToString().Contains(first, StringComparison.Ordinal) || result.ToString().Contains(second, StringComparison.Ordinal), "Titoktöredék szivárgott ki.");
        await fixture.AssertNoRawData(first);
    }

    [Fact]
    public async Task FiltersPrecedeWithheldCountAndLimitDoesNotAffectIt()
    {
        await using var fixture = new Fixture(Entry(1, "password=hidden", logger: "homeassistant.components.other") +
            Entry(2, "password=hidden") + Entry(3) + Entry(4, level: "WARNING") + Entry(5));
        using var call = await fixture.Call(new { level = "ERROR", integration = "demo", limit = 1 });
        var data = call.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(1, data.GetProperty("withheld_count").GetInt32());
        Assert.Equal(1, data.GetProperty("returned_count").GetInt32());
        Assert.Contains("5:", data.GetProperty("items")[0].GetProperty("messages")[0].GetString());
    }

    [Theory]
    [InlineData("auth")]
    [InlineData("id")]
    [InlineData("success")]
    [InlineData("binary")]
    [InlineData("fragments")]
    [InlineData("authsize")]
    [InlineData("depth")]
    [InlineData("duplicate")]
    public async Task ProtocolFailuresAreBoundedSafeAndDisposed(string variant)
    {
        await using var fixture = new Fixture(Entry(1));
        var normal = new[] { "{\"type\":\"auth_required\"}", "{\"type\":\"auth_ok\"}", "{\"type\":\"result\",\"id\":1,\"success\":true,\"result\":[]}" };
        if (variant == "auth") normal[1] = "{\"type\":\"auth_invalid\",\"message\":\"hidden\"}";
        if (variant == "id") normal[2] = "{\"type\":\"result\",\"id\":2,\"success\":true,\"result\":[]}";
        if (variant == "success") normal[2] = "{\"type\":\"result\",\"id\":1,\"success\":false,\"error\":{\"message\":\"hidden\"}}";
        if (variant == "authsize") normal[0] = "{\"type\":\"auth_required\",\"x\":\"" + new string('x', 4_096) + "\"}";
        if (variant == "depth") normal[2] = "{\"x\":" + new string('[', 20) + "0" + new string(']', 20) + "}";
        if (variant == "duplicate") normal[2] = "{\"type\":\"result\",\"id\":2,\"id\":1,\"success\":true,\"result\":[]}";
        if (variant == "binary") fixture.Sockets.MessageType = System.Net.WebSockets.WebSocketMessageType.Binary;
        if (variant == "fragments") fixture.Sockets.NeverEnd = true;
        fixture.Sockets.Frame = frame => Encoding.UTF8.GetBytes(normal[frame]);
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("A Home Assistant naplólekérés sikertelen.", result.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(1, fixture.Sockets.Calls);
        Assert.True(fixture.Sockets.Last!.Disposed && fixture.Sockets.Last.Aborted);
        await fixture.AssertNoRawData("hidden");
    }

    [Theory]
    [InlineData("message", "\"not-an-array\"")]
    [InlineData("message", "[null]")]
    [InlineData("count", "0")]
    [InlineData("count", "1.5")]
    [InlineData("timestamp", "1e999")]
    [InlineData("first_occurred", "-1")]
    [InlineData("name", "null")]
    [InlineData("level", "\"INFO\"")]
    [InlineData("exception", "{}")]
    public async Task InvalidStructuredSchemaIsNotInventedOrReturned(string field, string invalid)
    {
        using var original = JsonDocument.Parse(Entry(1));
        var record = original.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        using var replacement = JsonDocument.Parse(invalid);
        record[field] = replacement.RootElement.Clone();
        await using var fixture = new Fixture(JsonSerializer.Serialize(record));
        using var call = await fixture.Call(new { });
        Assert.True(call.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData("https://ha.example:8443/", false, "wss://ha.example:8443/api/websocket")]
    [InlineData("http://192.168.1.5:8123/", true, "ws://192.168.1.5:8123/api/websocket")]
    public async Task ValidatedOriginAndCachedLeaseAreUsedWithoutRetry(string origin, bool allowHttp, string expected)
    {
        await using var secrets = new CachedSecretProvider(new HomeAssistantReadToolsTests.RuntimeSecrets(), TimeProvider.System);
        var id = Guid.NewGuid();
        var lease = await secrets.GetSecretLeaseAsync(id);
        var sockets = new HomeAssistantLogTestSocketFactory(() => "[]", () => lease.Value);
        using var client = new HomeAssistantClient(Options.Create(new HomeAssistantOptions
            { BaseUrl = origin, AllowHttp = allowHttp, TokenSecretId = id.ToString() }), secrets, new HomeAssistantReadToolsTests.Handler(""), sockets);
        await client.GetLogsAsync();
        Assert.Equal(new Uri(expected), sockets.Target);
        Assert.Same(lease, await secrets.GetSecretLeaseAsync(id));
        sockets.Frame = frame => Encoding.UTF8.GetBytes(frame == 0 ? "{\"type\":\"auth_required\"}" : "{\"type\":\"auth_invalid\"}");
        await Assert.ThrowsAsync<HomeAssistantException>(() => client.GetLogsAsync());
        Assert.Equal(2, sockets.Calls);
        Assert.NotSame(lease, await secrets.GetSecretLeaseAsync(id));
    }
}
