using System.Security.Cryptography;
using AgentGate.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantControlProtocolTests
{
    [Fact]
    public async Task ControlPostsOneCanonicalBodyAndReturnsOnlyTargetProjection()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secrets = new HomeAssistantReadToolsTests.RuntimeSecrets();
        var calls = new List<string>();
        using var handler = new HomeAssistantReadToolsTests.Handler("{}");
        handler.Respond = async (request, ct) =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Method == System.Net.Http.HttpMethod.Post)
            {
                using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("light.room", body.RootElement.GetProperty("entity_id").GetString());
                Assert.Equal(50, body.RootElement.GetProperty("brightness_pct").GetDouble());
                Assert.Equal(2, body.RootElement.EnumerateObject().Count());
                return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("[{\"entity_id\":\"lock.other\",\"state\":\"unlocked\"}]") };
            }
            return new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent("{\"entity_id\":\"light.room\",\"state\":\"on\",\"attributes\":{\"friendly_name\":\"Nappali\",\"password\":\"hidden\",\"nested\":{\"value\":1}}}") };
        };
        await using var app = new McpTestApplication(token, services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton(HomeAssistantControlPolicyTests.Policy());
            services.AddSingleton(_ => new AgentGate.Integrations.HomeAssistant.HomeAssistantClient(Options.Create(new AgentGate.Integrations.HomeAssistant.HomeAssistantOptions
            {
                BaseUrl = "https://ha.example/",
                TokenSecretId = Guid.NewGuid().ToString(),
                DomainAllowlist = ["light"]
            }), secrets, handler));
        });
        using var client = HomeAssistantToolProtocolTests.CreateClient(app, token);
        using var call = await HomeAssistantToolProtocolTests.SendAsync(client, "tools/call", new { name = "ha_control", arguments = new { domain = "light", service = "turn_on", entity_id = "light.room", data = new { brightness_pct = 50 } } });
        var result = call.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.Equal("on", result.GetProperty("structuredContent").GetProperty("state").GetString());
        Assert.False(result.GetRawText().Contains("lock.other", StringComparison.Ordinal));
        Assert.False(result.GetRawText().Contains("hidden", StringComparison.Ordinal));
        Assert.Equal(new[] { "POST /api/services/light/turn_on", "GET /api/states/light.room" }, calls);
    }

    [Fact]
    public async Task UnknownTopLevelArgumentCannotBeDiscardedBySdkBinder()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var secrets = new HomeAssistantReadToolsTests.RuntimeSecrets();
        using var handler = new HomeAssistantReadToolsTests.Handler("{\"entity_id\":\"light.room\",\"state\":\"on\",\"attributes\":{}}");
        await using var app = new McpTestApplication(token, services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton(HomeAssistantControlPolicyTests.Policy());
            services.AddSingleton(_ => new AgentGate.Integrations.HomeAssistant.HomeAssistantClient(Options.Create(new AgentGate.Integrations.HomeAssistant.HomeAssistantOptions
            {
                BaseUrl = "https://ha.example/",
                TokenSecretId = Guid.NewGuid().ToString(),
                DomainAllowlist = ["light"]
            }), secrets, handler));
        });
        using var client = HomeAssistantToolProtocolTests.CreateClient(app, token);
        using var call = await HomeAssistantToolProtocolTests.SendAsync(client, "tools/call", new { name = "ha_control", arguments = new { domain = "light", service = "turn_on", entity_id = "light.room", target = new { entity_id = "all" } } });
        Assert.True(call.RootElement.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, secrets.Calls);
    }

    [Fact]
    public async Task AuditProjectsOnlyValidatedControlDataScalars()
    {
        await using var f = new ControlFixture { Entity = "light.room" };
        using var reply = await f.Call("ha_control", "light", "turn_on", "{\"brightness_pct\":50}");
        Assert.False(ControlFixture.Failed(reply));
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.App.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SanitizedParameters FROM AuditCalls WHERE Tool='ha_control'";
        using var parameters = System.Text.Json.JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        Assert.Equal("50", parameters.RootElement.GetProperty("data.brightness_pct").GetString());
        Assert.False(parameters.RootElement.TryGetProperty("data", out _));
    }

    [Theory]
    [MemberData(nameof(HomeAssistantControlPolicyTests.Matrix), MemberType = typeof(HomeAssistantControlPolicyTests))]
    public async Task EveryApprovedMatrixEntryUsesExactServicePostPath(string domain, string service, RiskLevel risk, string data)
    {
        await using var f = new ControlFixture
        {
            Entity = domain + ".room",
            Attributes = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                device_class = risk == RiskLevel.SensitiveControl ? "garage" : "blind",
                temperature_unit = "°C",
                min_temp = 10,
                max_temp = 30
            })
        };
        using var reply = await f.Call(risk == RiskLevel.Control ? "ha_control" : "ha_sensitive_control", domain, service, data);
        Assert.False(ControlFixture.Failed(reply));
        if (risk == RiskLevel.SensitiveControl) Assert.Equal(AgentGate.Approval.ApprovalState.Executed, (await f.Channel.Approve()).State);
        Assert.Equal(1, f.Posts);
        Assert.Contains("POST /api/services/" + domain + "/" + service, f.Calls);
        Assert.Equal("GET /api/states/" + domain + ".room", f.Calls.Last());
    }

    [Theory]
    [InlineData("ha_control", RiskLevel.Control)]
    [InlineData("ha_sensitive_control", RiskLevel.SensitiveControl)]
    public async Task MissingControlIsListedWithFixedRiskAndFailsSafely(string name, RiskLevel risk)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var app = new McpTestApplication(token, services => services.AddLogging(logging => logging.ClearProviders()));
        using var client = HomeAssistantToolProtocolTests.CreateClient(app, token);
        using var list = await HomeAssistantToolProtocolTests.SendAsync(client, "tools/list", new { });
        var tool = Assert.Single(list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
            item => item.GetProperty("name").GetString() == name);
        Assert.False(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        var registered = Assert.Single(app.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!, item => item.ProtocolTool.Name == name);
        Assert.Equal(risk, Assert.Single(registered.Metadata.OfType<ToolRiskAttribute>()).Level);
        using var call = await HomeAssistantToolProtocolTests.SendAsync(client, "tools/call", new { name, arguments = new { domain = "light", service = "turn_on", entity_id = "light.room" } });
        Assert.True(call.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }
}
