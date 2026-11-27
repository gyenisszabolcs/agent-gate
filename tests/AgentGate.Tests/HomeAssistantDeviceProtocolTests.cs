using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantDeviceProtocolTests
{
    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "malformed")]
    [InlineData(true, "malformed")]
    [InlineData(false, "http")]
    [InlineData(true, "http")]
    public async Task StateDevicesTraverseActualMcpAndDurableAudit(bool get, string outcome)
    {
        var token = Guid.NewGuid().ToString("N");
        var secrets = new HomeAssistantReadToolsTests.RuntimeSecrets();
        using var handler = new HomeAssistantReadToolsTests.Handler("{}");
        handler.Respond = async (request, ct) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path == "/api/template" ? await request.Content!.ReadAsStringAsync(ct) : "";
            var devices = body.Contains("device_attr", StringComparison.Ordinal);
            if (devices && outcome == "http") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var payload = path != "/api/template"
                ? path == "/api/states" ? "[" + HomeAssistantDeviceReadTests.State + "]" : HomeAssistantDeviceReadTests.State
                : devices ? outcome switch
                {
                    "malformed" => "[]",
                    "missing" => """[{"entity_id":"light.reka","device":null}]""",
                    _ => HomeAssistantDeviceReadTests.Metadata
                } : "[]";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) };
        };
        await using var app = new McpTestApplication(token, services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton(_ => new HomeAssistantClient(Options.Create(new HomeAssistantOptions
            {
                BaseUrl = "https://ha.example/", TokenSecretId = Guid.NewGuid().ToString(), DomainAllowlist = ["light"]
            }), secrets, handler));
        });
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        using var list = await HomeAssistantToolProtocolTests.SendAsync(client, "tools/list", new { });
        var definition = Assert.Single(list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "ha_list_states");
        var schema = definition.GetProperty("inputSchema");
        Assert.Equal(new[] { "string", "null" }, schema.GetProperty("properties").GetProperty("device_id").GetProperty("type").EnumerateArray().Select(item => item.GetString()));
        if (schema.TryGetProperty("required", out var required))
            Assert.DoesNotContain(required.EnumerateArray(), item => item.GetString() == "device_id");
        var name = get ? "ha_get_state" : "ha_list_states";
        var arguments = get ? new Dictionary<string, string> { ["entity_id"] = "light.reka" }
            : outcome == "missing" ? new Dictionary<string, string> { ["domain"] = "light" }
            : new Dictionary<string, string> { ["domain"] = "light", ["device_id"] = "yeelight_reka" };
        using var call = await HomeAssistantToolProtocolTests.SendAsync(client, "tools/call", new { name, arguments });
        var result = call.RootElement.GetProperty("result");
        var failed = result.TryGetProperty("isError", out var error) && error.GetBoolean();
        Assert.Equal(outcome is "malformed" or "http", failed);
        if (!failed)
        {
            var data = result.GetProperty("structuredContent");
            var device = (get ? data : Assert.Single(data.GetProperty("items").EnumerateArray())).GetProperty("device");
            if (outcome == "missing") Assert.Equal(JsonValueKind.Null, device.ValueKind);
            else
            {
                Assert.Equal("yeelight_reka", device.GetProperty("device_id").GetString());
                Assert.Equal("Yeelight Réka", device.GetProperty("name").GetString());
                Assert.Equal("reka", device.GetProperty("area").GetProperty("area_id").GetString());
            }
        }
        else Assert.Equal("A Home Assistant lekérés sikertelen.", result.GetProperty("content")[0].GetProperty("text").GetString());
        await using var connection = new SqliteConnection($"Data Source={app.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Tool, State, SanitizedParameters FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(name, reader.GetString(0));
        Assert.Equal(failed ? "Failed" : "Succeeded", reader.GetString(1));
        using var parameters = JsonDocument.Parse(reader.GetString(2));
        Assert.Equal(arguments.Keys, parameters.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.False(await reader.ReadAsync());
        Assert.False(call.RootElement.GetRawText().Contains(secrets.Token, StringComparison.Ordinal), "Titok az MCP-válaszban.");
    }
}
