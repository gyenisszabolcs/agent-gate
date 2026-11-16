using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentGate.Audit;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Policy;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantToolProtocolTests
{
    [Theory]
    [InlineData("ha_list_states", "domain")]
    [InlineData("ha_get_state", "entity_id")]
    [InlineData("ha_get_config", null)]
    public async Task MissingIntegrationIsListedReadOnlyAndFailsThroughDurableAudit(string name, string? parameter)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var factory = new McpTestApplication(token, services => services.AddLogging(logging => logging.ClearProviders()));
        using var client = CreateClient(factory, token);
        using var list = await SendAsync(client, "tools/list", new { });
        var tool = Assert.Single(list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
            item => item.GetProperty("name").GetString() == name);
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        var registered = Assert.Single(factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!,
            item => item.ProtocolTool.Name == name);
        Assert.Equal(RiskLevel.Read, Assert.Single(registered.Metadata.OfType<ToolRiskAttribute>()).Level);
        Assert.Equal(parameter is null ? [] : new[] { parameter }, Assert.Single(registered.Metadata.OfType<ToolAuditParametersAttribute>()).Names);
        using var call = await SendAsync(client, "tools/call", new { name, arguments = parameter == "entity_id" ? new Dictionary<string, string> { [parameter] = "sensor.room" } : new() });
        var result = call.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("A Home Assistant nincs beállítva.", result.GetProperty("content")[0].GetProperty("text").GetString());
        await using var connection = new SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Tool, State, SanitizedError FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(name, reader.GetString(0));
        Assert.Equal("Failed", reader.GetString(1));
        Assert.Equal("A Home Assistant nincs beállítva.", reader.GetString(2));
        Assert.False(await reader.ReadAsync());
        Assert.Null(factory.Services.GetService<HomeAssistantClient>());
    }

    [Theory]
    [InlineData("ha_list_states", "success")]
    [InlineData("ha_get_state", "success")]
    [InlineData("ha_get_config", "success")]
    [InlineData("ha_list_states", "secret")]
    [InlineData("ha_get_state", "http")]
    [InlineData("ha_get_config", "timeout")]
    public async Task ConfiguredToolsUseRealProtocolAndDurableAuditWithoutSecrets(string name, string outcome)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secrets = new HomeAssistantReadToolsTests.RuntimeSecrets();
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var logs = new Audit.McpAuditTests.RecordingLogger();
        using var handler = new HomeAssistantReadToolsTests.Handler("{}");
        handler.Respond = (request, _) =>
        {
            if (outcome == "http") throw new HttpRequestException(canary);
            var attributes = new Dictionary<string, object?>
            {
                ["friendly_name"] = "Nappali",
                ["icon"] = secrets.Token,
                ["password"] = canary,
                [secrets.Token] = secrets.Token,
                ["nested"] = new[] { new { gps = "47.123,19.456", token = canary } }
            };
            var state = new { entity_id = "sensor.room", state = "21", attributes };
            object payload = request.RequestUri!.AbsolutePath switch
            {
                "/api/states" => new[] { state },
                "/api/config" => new
                {
                    version = "2026.11",
                    location_name = secrets.Token,
                    token = secrets.Token,
                    unit_system = new { length = "km", wind_speed = "km/h" },
                    latitude = 47.123
                },
                _ => state
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) });
        };
        if (outcome == "secret") secrets.Read = _ => throw new InvalidOperationException(canary);
        if (outcome == "timeout") secrets.Read = async ct => { await Task.Delay(Timeout.Infinite, ct); return secrets.Token; };
        await using var factory = new McpTestApplication(token, services =>
        {
            services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddSingleton(_ => new HomeAssistantClient(Options.Create(new HomeAssistantOptions
            {
                BaseUrl = "https://ha.example/",
                TokenSecretId = Guid.NewGuid().ToString(),
                DomainAllowlist = ["sensor"],
                Timeout = TimeSpan.FromMilliseconds(80)
            }), secrets, handler));
        });
        using var client = CreateClient(factory, token);
        var arguments = new Dictionary<string, string>();
        if (name == "ha_get_state") arguments["entity_id"] = "sensor.room";
        if (name == "ha_list_states") arguments["domain"] = "sensor";
        using var call = await SendAsync(client, "tools/call", new { name, arguments });
        var result = call.RootElement.GetProperty("result");
        var failed = result.TryGetProperty("isError", out var error) && error.GetBoolean();
        Assert.Equal(outcome != "success", failed);
        if (failed) Assert.Equal("A Home Assistant lekérés sikertelen.", result.GetProperty("content")[0].GetProperty("text").GetString());
        else
        {
            var data = result.GetProperty("structuredContent");
            Assert.True(data.TryGetProperty(name == "ha_list_states" ? "returned_count" : name == "ha_get_state" ? "state" : "version", out _),
                "Strukturált mezőnevek: " + string.Join(",", data.EnumerateObject().Select(property => property.Name)));
            if (name == "ha_list_states") Assert.Equal(1, data.GetProperty("returned_count").GetInt32());
            if (name == "ha_get_state") Assert.Equal("21", data.GetProperty("state").GetString());
            if (name == "ha_get_config") Assert.Equal("2026.11", data.GetProperty("version").GetString());
        }
        Assert.False(call.RootElement.ToString().Contains(secrets.Token, StringComparison.Ordinal), "Titok az MCP-válaszban.");
        await using var connection = new SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Tool, State, SanitizedParameters, SanitizedError FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(name, reader.GetString(0));
        Assert.Equal(failed ? "Failed" : "Succeeded", reader.GetString(1));
        using var parameters = JsonDocument.Parse(reader.GetString(2));
        Assert.Equal(arguments.Keys, parameters.RootElement.EnumerateObject().Select(property => property.Name));
        for (var index = 0; index < reader.FieldCount; index++)
            Assert.False(reader.GetValue(index).ToString()!.Contains(secrets.Token, StringComparison.Ordinal) ||
                reader.GetValue(index).ToString()!.Contains(canary, StringComparison.Ordinal), "Titok a tartós auditban.");
        Assert.False(await reader.ReadAsync());
        Assert.False(logs.Contains(secrets.Token) || logs.Contains(canary), "Titok a strukturált naplóban.");
        Assert.False(call.RootElement.ToString().Contains(canary, StringComparison.Ordinal), "Provider-titok az MCP-válaszban.");
        Assert.Equal(outcome is "secret" or "timeout" ? 0 : 1, handler.Calls);
    }

    [Theory]
    [InlineData("ha_list_states", "domain", "lock")]
    [InlineData("ha_list_states", "domain", "sensor/../config")]
    [InlineData("ha_get_state", "entity_id", "lock.front")]
    [InlineData("ha_get_state", "entity_id", "sensor.bad/path")]
    public async Task RejectedInputsAreDurablyAuditedWithoutProviderOrHttp(string name, string parameter, string value)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secrets = new HomeAssistantReadToolsTests.RuntimeSecrets();
        using var handler = new HomeAssistantReadToolsTests.Handler("{}");
        await using var factory = new McpTestApplication(token, services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton(_ => new HomeAssistantClient(Options.Create(new HomeAssistantOptions
            {
                BaseUrl = "https://ha.example/",
                TokenSecretId = Guid.NewGuid().ToString(),
                DomainAllowlist = ["sensor"]
            }), secrets, handler));
        });
        using var client = CreateClient(factory, token);
        using var call = await SendAsync(client, "tools/call", new { name, arguments = new Dictionary<string, string> { [parameter] = value } });
        Assert.True(call.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(0, secrets.Calls);
        Assert.Equal(0, handler.Calls);
        await using var connection = new SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Tool, State, SanitizedParameters FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(name, reader.GetString(0));
        Assert.Equal("Failed", reader.GetString(1));
        using var parameters = JsonDocument.Parse(reader.GetString(2));
        Assert.Equal(parameter, Assert.Single(parameters.RootElement.EnumerateObject()).Name);
        Assert.False(await reader.ReadAsync());
    }

    internal static HttpClient CreateClient(McpTestApplication factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
        return client;
    }

    internal static async Task<JsonDocument> SendAsync(HttpClient client, string method, object parameters, CancellationToken cancellationToken = default)
    {
        using var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters }, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = body.Split('\n').Last(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("error", out _));
        return document;
    }
}
