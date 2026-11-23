using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentGate.Audit;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Policy;
using AgentGate.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantLogsTests
{
    [Fact]
    public async Task StructuredSystemLogContractIncludesWithheldCount()
    {
        await using var fixture = new Fixture("");
        using var call = await fixture.Call(new { });
        var data = call.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.True(data.TryGetProperty("withheld_count", out _), "Hiányzik a bejegyzésszintű visszatartási számláló.");
    }

    internal static string Entry(int index, string message = "rendben", string level = "ERROR", string logger = "homeassistant.components.demo",
        string? exception = null, string[]? messages = null, long count = 1) =>
        JsonSerializer.Serialize(new { name = logger, level, message = messages ?? [$"{index}: {message}"], count,
            first_occurred = 1_000d + index, timestamp = 2_000d + index, exception = exception ?? "" }) + "\n";

    [Fact]
    public async Task ReadRouteReturnsLatestMatchesAndAuditsOnlyArguments()
    {
        await using var fixture = new Fixture(Entry(1) + Entry(2, "más", "WARNING") + Entry(3) + Entry(4));
        using var call = await fixture.Call(new { limit = 2, level = "ERROR", integration = "demo" });
        var result = call.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean());
        var data = result.GetProperty("structuredContent");
        var items = data.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Contains("3: rendben", items[0].GetProperty("messages")[0].GetString());
        Assert.Contains("4: rendben", items[1].GetProperty("messages")[0].GetString());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Equal("ERROR", items[0].GetProperty("level").GetString());
        Assert.Equal("demo", items[0].GetProperty("integration").GetString());
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Equal(1, fixture.Sockets.Calls);
        Assert.True(fixture.Sockets.Last!.Disposed && fixture.Sockets.Last.Aborted);
        Assert.Equal(1, fixture.Secrets.Calls);
        Assert.True(fixture.FixedRequest);
        var tool = Assert.Single(fixture.Factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!,
            tool => tool.ProtocolTool.Name == "ha_get_logs");
        Assert.Equal(RiskLevel.Read, Assert.Single(tool.Metadata.OfType<ToolRiskAttribute>()).Level);
        Assert.Equal(new[] { "limit", "level", "integration" }, Assert.Single(tool.Metadata.OfType<ToolAuditParametersAttribute>()).Names);
        using var list = await HomeAssistantToolProtocolTests.SendAsync(fixture.Client, "tools/list", new { });
        var listed = Assert.Single(list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "ha_get_logs");
        Assert.True(listed.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.False(listed.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        await using var connection = new SqliteConnection($"Data Source={fixture.Factory.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State, SanitizedParameters, SanitizedError FROM AuditCalls WHERE Tool = 'ha_get_logs'";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Succeeded", reader.GetString(0));
        using var parameters = JsonDocument.Parse(reader.GetString(1));
        Assert.Equal(new[] { "integration", "level", "limit" }, parameters.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain("rendben", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.False(await reader.ReadAsync());
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("unicode")]
    [InlineData("escaped")]
    [InlineData("many")]
    public async Task SerializedBudgetIncludesBothCopiesMetadataAndTruncation(string variant)
    {
        var body = variant switch
        {
            "unicode" => Entry(1, string.Concat(Enumerable.Repeat("😀ő", 12_000))),
            "escaped" => Entry(1, new string('"', 40_000)),
            "many" => string.Concat(Enumerable.Range(0, 30).Select(index => Entry(index, new string('x', 1_500)))),
            _ => Entry(1, new string('x', 50_000))
        };
        await using var fixture = new Fixture(body);
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        var data = result.GetProperty("structuredContent");
        Assert.InRange(Encoding.UTF8.GetByteCount(result.GetRawText()), 1, 32_000);
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.False(data.GetProperty("redacted").GetBoolean());
        Assert.Equal(data.GetRawText(), result.GetProperty("content")[0].GetProperty("text").GetString());
        var items = data.GetProperty("items");
        Assert.Equal(items.GetArrayLength(), data.GetProperty("returned_count").GetInt32());
        Assert.InRange(items.GetArrayLength(), 1, 20);
        Assert.DoesNotContain("\uFFFD", items[0].GetProperty("messages")[0].GetString());
        if (variant == "many") Assert.Contains("29:", items[items.GetArrayLength() - 1].GetProperty("messages")[0].GetString());
    }

    [Fact]
    public async Task OversizedIntegrationMetadataCannotBypassSerializedBudgetWithEmptyText()
    {
        var body = Entry(1, logger: "homeassistant.components." + new string('a', 24_000));
        Assert.InRange(body.Length, 1, 65_536);
        await using var fixture = new Fixture(body);
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.InRange(Encoding.UTF8.GetByteCount(result.GetRawText()), 1, 32_000);
        var data = result.GetProperty("structuredContent");
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.False(data.GetProperty("redacted").GetBoolean());
        Assert.Equal(0, data.GetProperty("returned_count").GetInt32());
        Assert.Equal(0, data.GetProperty("items").GetArrayLength());
        Assert.Equal(data.GetRawText(), result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task DefaultAndSmallLimitsKeepLatestInReversedSourceOrder(int requested, int expected)
    {
        await using var fixture = new Fixture(string.Concat(Enumerable.Range(0, 130).Select(index => Entry(index))));
        using var call = await fixture.Call(requested < 0 ? new { } : new { limit = requested });
        var data = call.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(expected, data.GetProperty("returned_count").GetInt32());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.Contains($"{130 - expected}: rendben", data.GetProperty("items")[0].GetProperty("messages")[0].GetString());
        Assert.Contains("129: rendben", data.GetProperty("items")[expected - 1].GetProperty("messages")[0].GetString());
    }

    [Fact]
    public async Task MaximumRequestedLimitStillHonorsSerializedBudget()
    {
        var body = string.Concat(Enumerable.Range(0, 130).Select(index => JsonSerializer.Serialize(new
            { name = "a", level = "WARNING", message = new[] { index.ToString() }, count = 1, first_occurred = 0, timestamp = 0, exception = "" }) + "\n"));
        await using var fixture = new Fixture(body);
        using var call = await fixture.Call(new { limit = 100 });
        var data = call.RootElement.GetProperty("result").GetProperty("structuredContent");
        // A gazdagabb strukturált metaadat miatt a bájtkeret 100 előtt is elfogyhat.
        Assert.InRange(data.GetProperty("returned_count").GetInt32(), 1, 100);
        Assert.InRange(Encoding.UTF8.GetByteCount(call.RootElement.GetProperty("result").GetRawText()), 1, 32_000);
        Assert.True(data.GetProperty("truncated").GetBoolean());
        var items = data.GetProperty("items");
        Assert.Equal("129", items[items.GetArrayLength() - 1].GetProperty("messages")[0].GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("2147483648")]
    [InlineData("1e999")]
    [InlineData("1.5")]
    public async Task InvalidLimitIsRejectedBeforeSecretsOrHttp(string json)
    {
        await using var fixture = new Fixture(Entry(1));
        using var arguments = JsonDocument.Parse("{\"limit\":" + json + "}");
        using var call = await fixture.Call(arguments.RootElement);
        Assert.True(call.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(0, fixture.Secrets.Calls);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("level", "error")]
    [InlineData("level", ".*")]
    [InlineData("level", "INFO")]
    [InlineData("level", "DEBUG")]
    [InlineData("integration", "../config")]
    [InlineData("integration", "demo\n")]
    public async Task FiltersAreValidatedDataNotRegexOrPaths(string name, string value)
    {
        await using var fixture = new Fixture(Entry(1));
        using var call = await fixture.Call(new Dictionary<string, string> { [name] = value });
        Assert.True(call.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(0, fixture.Secrets.Calls);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task EmptyStructuredLogHasNoInventedMetadata()
    {
        await using var fixture = new Fixture("");
        using var call = await fixture.Call(new { });
        var data = call.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.False(data.GetProperty("truncated").GetBoolean());
        var items = data.GetProperty("items");
        Assert.Equal(0, items.GetArrayLength());
        Assert.Equal(0, data.GetProperty("withheld_count").GetInt32());
    }

    [Fact]
    public async Task AggregatedMessagesTracebackAndTypedMetadataArePreserved()
    {
        var trace = "Traceback (most recent call last):\n  File demo.py, line 1\nKeyError: keyboard";
        await using var fixture = new Fixture(Entry(1, exception: trace, messages: ["első", "második"], count: 7) +
            Entry(2, "kész", "WARNING", "homeassistant.core"));
        using var call = await fixture.Call(new { });
        var items = call.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(trace, items[0].GetProperty("exception").GetString());
        Assert.Equal(2, items[0].GetProperty("messages").GetArrayLength());
        Assert.Equal(7, items[0].GetProperty("count").GetInt64());
        Assert.Equal(1001, items[0].GetProperty("first_occurred").GetDouble());
        Assert.Equal(2001, items[0].GetProperty("timestamp").GetDouble());
        Assert.Equal("WARNING", items[1].GetProperty("level").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("integration").ValueKind);
    }

    [Theory]
    [InlineData("known")]
    [InlineData("split")]
    [InlineData("quoted")]
    [InlineData("fulltextlimit")]
    public async Task WholeInputProtectionPrecedesSplittingAndOutputClipping(string variant)
    {
        await using var fixture = new Fixture("");
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        fixture.Body = variant switch
        {
            "known" => Entry(1, new string('x', 8_100) + fixture.Secrets.Token + new string('x', 20_000)),
            "split" => Entry(1, fixture.Secrets.Token[..32] + "\u2028" + fixture.Secrets.Token[32..]),
            "quoted" => Entry(1, "password=\"ő😀\n" + canary + "\n" + Entry(2) + "\""),
            _ => Entry(1, new string('x', 65_537) + canary)
        };
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        Assert.False(result.ToString().Contains(fixture.Secrets.Token[..16], StringComparison.Ordinal) ||
            result.ToString().Contains(fixture.Secrets.Token[^16..], StringComparison.Ordinal) ||
            result.ToString().Contains(canary, StringComparison.Ordinal), "Titok vagy titoktöredék az MCP-válaszban.");
        var data = result.GetProperty("structuredContent");
        Assert.True(data.GetProperty("redacted").GetBoolean());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        if (variant != "known") Assert.Equal(0, data.GetProperty("returned_count").GetInt32());
        await fixture.AssertNoRawData(canary);
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("close")]
    [InlineData("provider")]
    [InlineData("timeout")]
    [InlineData("oversized")]
    [InlineData("utf8")]
    [InlineData("missing")]
    public async Task FailuresAreFixedSafeAndNeverReturnProviderBody(string failure)
    {
        await using var fixture = new Fixture("", timeout: TimeSpan.FromMilliseconds(100), configured: failure != "missing");
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        if (failure == "provider") fixture.Secrets.Read = _ => throw new InvalidOperationException(canary);
        if (failure == "timeout") fixture.Secrets.Read = async ct => { await Task.Delay(Timeout.Infinite, ct); return fixture.Secrets.Token; };
        if (failure == "oversized") fixture.Body = new string('x', 1024 * 1024 + 1);
        if (failure == "connect") fixture.Sockets.BeforeConnect = _ => throw new InvalidOperationException(canary);
        if (failure == "close") fixture.Sockets.MessageType = System.Net.WebSockets.WebSocketMessageType.Close;
        if (failure == "utf8") fixture.Sockets.Frame = _ => [0xFF, 0xFE];
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var expectedError = failure == "missing" ? "A Home Assistant nincs beállítva." : "A Home Assistant naplólekérés sikertelen.";
        Assert.True(string.Equals(expectedError, result.GetProperty("content")[0].GetProperty("text").GetString(), StringComparison.Ordinal),
            "A hibaválasz nem az elvárt fix üzenet.");
        Assert.False(result.ToString().Contains(canary, StringComparison.Ordinal), "Provider-adat a hibaválaszban.");
        await fixture.AssertNoRawData(canary);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellationReachesSecretOrSocketConnectWithSafeException(bool inSecret)
    {
        await using var fixture = new Fixture("");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Block(CancellationToken ct)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw new OperationCanceledException(fixture.Secrets.Token); }
            return fixture.Secrets.Token;
        }
        if (inSecret) fixture.Secrets.Read = Block;
        else fixture.Sockets.BeforeConnect = async ct => { await Block(ct); };
        using var cancellation = new CancellationTokenSource();
        var pending = new HomeAssistantLogTools(fixture.Factory.Services).GetLogs(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(error.ToString().Contains(fixture.Secrets.Token, StringComparison.Ordinal), "Titok a megszakításban.");
        Assert.Equal(inSecret ? 0 : 1, fixture.Sockets.Calls);
    }

    [Fact]
    public async Task MixedExactAndWhitespaceSplitKnownTokenCannotBypassGuard()
    {
        await using var fixture = new Fixture("");
        var value = fixture.Secrets.Token;
        fixture.Body = Entry(1, value + " másolat " + value[..32] + "\n\u200B" + value[32..]);
        using var call = await fixture.Call(new { });
        var data = call.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.True(data.GetProperty("redacted").GetBoolean());
        Assert.Equal(0, data.GetProperty("returned_count").GetInt32());
        await fixture.AssertNoRawData(value[..16]);
    }

    [Fact]
    public async Task IntegrationFilterExcludesOtherAndUnknownEntriesBeforeLimit()
    {
        await using var fixture = new Fixture(Entry(1) + Entry(2, logger: "custom_components.other") +
            Entry(3, logger: "custom_components.demo.worker") + Entry(4, logger: "homeassistant.core"));
        using var call = await fixture.Call(new { integration = "demo", limit = 1 });
        var data = call.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(1, data.GetProperty("returned_count").GetInt32());
        Assert.Equal("demo", data.GetProperty("items")[0].GetProperty("integration").GetString());
        Assert.Contains("3: rendben", data.GetProperty("items")[0].GetProperty("messages")[0].GetString());
        Assert.True(data.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultReceiveHasDeadlineAndCallerCancellation(bool cancelByCaller)
    {
        await using var fixture = new Fixture("", timeout: cancelByCaller ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(150));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = false;
        fixture.Sockets.BeforeReceive = async (frame, ct) =>
        {
            if (frame < 2) return;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { observedCancellation = true; throw; }
        };
        using var cancellation = new CancellationTokenSource();
        var pending = new HomeAssistantLogTools(fixture.Factory.Services).GetLogs(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelByCaller)
        {
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<GateToolException>(() => pending);
            Assert.Equal("A Home Assistant naplólekérés sikertelen.", error.Message);
        }
        Assert.True(observedCancellation);
        Assert.True(fixture.Sockets.Last!.Disposed && fixture.Sockets.Last.Aborted);
    }


    internal sealed class Fixture : IAsyncDisposable
    {
        public HomeAssistantReadToolsTests.RuntimeSecrets Secrets { get; } = new();
        public HomeAssistantReadToolsTests.Handler Handler { get; }
        public McpTestApplication Factory { get; }
        public HttpClient Client { get; }
        public Audit.McpAuditTests.RecordingLogger Logs { get; } = new();
        public bool FixedRequest => Sockets.FixedRequest && Sockets.Target == new Uri("wss://ha.example/api/websocket");
        public HomeAssistantLogTestSocketFactory Sockets { get; }
        public string Body { get; set; }

        public Fixture(string body, TimeSpan? timeout = null, bool configured = true)
        {
            Body = body;
            Handler = new HomeAssistantReadToolsTests.Handler(body);
            Handler.Respond = (_, _) => throw new InvalidOperationException("A naplóolvasás nem használhat HTTP-fallbacket.");
            Sockets = new HomeAssistantLogTestSocketFactory(() => "[" + string.Join(',', Body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse()) + "]", () => Secrets.Token);
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            Factory = new McpTestApplication(token, services =>
            {
                services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
                if (configured) services.AddSingleton(_ => new HomeAssistantClient(Options.Create(new HomeAssistantOptions
                {
                    BaseUrl = "https://ha.example/", TokenSecretId = Guid.NewGuid().ToString(),
                    Timeout = timeout ?? TimeSpan.FromSeconds(5)
                }), Secrets, Handler, Sockets));
            });
            Client = HomeAssistantToolProtocolTests.CreateClient(Factory, token);
        }

        public Task<JsonDocument> Call(object arguments) => HomeAssistantToolProtocolTests.SendAsync(Client, "tools/call", new { name = "ha_get_logs", arguments });

        public async Task AssertNoRawData(string canary)
        {
            Assert.False(Logs.Contains(Secrets.Token) || Logs.Contains(canary), "Nyers adat az alkalmazásnaplóban.");
            await using var connection = new SqliteConnection($"Data Source={Factory.AuditPath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT SanitizedParameters, SanitizedError FROM AuditCalls";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (var index = 0; index < reader.FieldCount; index++)
                Assert.False(reader.GetValue(index).ToString()!.Contains(Secrets.Token, StringComparison.Ordinal) ||
                    reader.GetValue(index).ToString()!.Contains(canary, StringComparison.Ordinal), "Nyers adat a tartós auditban.");
            Assert.False(await reader.ReadAsync());
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            Handler.Dispose();
        }
    }
}
