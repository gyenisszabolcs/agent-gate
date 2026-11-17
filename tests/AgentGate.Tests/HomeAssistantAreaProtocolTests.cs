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

public sealed class HomeAssistantAreaProtocolTests
{
    [Fact]
    public async Task AreasToolUsesReadMetadataAndOnlyVisibleStateDescriptors()
    {
        using var fixture = HomeAssistantAreaTests.CreateFixture();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var factory = CreateApplication(token, fixture);
        using var client = HomeAssistantToolProtocolTests.CreateClient(factory, token);
        using var list = await HomeAssistantToolProtocolTests.SendAsync(client, "tools/list", new { });
        var tool = Assert.Single(list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "ha_list_areas");
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        var registered = Assert.Single(factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!,
            item => item.ProtocolTool.Name == "ha_list_areas");
        Assert.Equal(RiskLevel.Read, Assert.Single(registered.Metadata.OfType<ToolRiskAttribute>()).Level);
        Assert.Empty(Assert.Single(registered.Metadata.OfType<ToolAuditParametersAttribute>()).Names);
        using var call = await HomeAssistantToolProtocolTests.SendAsync(client, "tools/call", new { name = "ha_list_areas", arguments = new { } });
        var items = call.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        var living = items.EnumerateArray().Single(item => item.GetProperty("area_id").GetString() == "living");
        Assert.Equal("Nappali", living.GetProperty("area_name").GetString());
        var entities = living.GetProperty("entities").EnumerateArray().ToArray();
        Assert.Equal(new[] { "light.desk", "sensor.room" }, entities.Select(item => item.GetProperty("entity_id").GetString()));
        Assert.Equal("Hőmérő", entities[1].GetProperty("friendly_name").GetString());
        Assert.All(entities, item => Assert.Equal(new[] { "entity_id", "friendly_name" }, item.EnumerateObject().Select(property => property.Name).Order()));
        Assert.Empty(items.EnumerateArray().Single(item => item.GetProperty("area_id").GetString() == "empty").GetProperty("entities").EnumerateArray());
        await AssertAudit(factory, "ha_list_areas", new Dictionary<string, string>());
    }

    [Theory]
    [InlineData("living", "sensor", "sensor.room")]
    [InlineData("Nappali", null, "light.desk,sensor.room")]
    [InlineData("unknown", null, "")]
    [InlineData("empty", "sensor", "")]
    public async Task AreaNameOrIdFiltersIntersectReadVisibilityAndDomain(string area, string? domain, string expected)
    {
        using var fixture = HomeAssistantAreaTests.CreateFixture();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var factory = CreateApplication(token, fixture);
        using var client = HomeAssistantToolProtocolTests.CreateClient(factory, token);
        var arguments = new Dictionary<string, string> { ["area"] = area };
        if (domain is not null) arguments["domain"] = domain;
        using var call = await HomeAssistantToolProtocolTests.SendAsync(client, "tools/call", new { name = "ha_list_states", arguments });
        var result = call.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(expected, string.Join(',', result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("entity_id").GetString())));
        Assert.False(result.GetProperty("truncated").GetBoolean());
        await AssertAudit(factory, "ha_list_states", arguments);
    }

    [Fact]
    public async Task AreaFilterIsSecretRedactedInDurableAudit()
    {
        using var fixture = HomeAssistantAreaTests.CreateFixture();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var factory = CreateApplication(token, fixture);
        using var client = HomeAssistantToolProtocolTests.CreateClient(factory, token);
        using var call = await HomeAssistantToolProtocolTests.SendAsync(client, "tools/call", new { name = "ha_list_states", arguments = new { area = "password=" + canary } });
        Assert.Equal(0, call.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("returned_count").GetInt32());
        await using var connection = new SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SanitizedParameters FROM AuditCalls";
        var parameters = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.False(parameters.Contains(canary, StringComparison.Ordinal), "Titok a területszűrő auditjában.");
        using var document = JsonDocument.Parse(parameters);
        Assert.True(document.RootElement.TryGetProperty("area", out _));
    }

    private static McpTestApplication CreateApplication(string token, HomeAssistantReadToolsTests.Fixture fixture) => new(token, services =>
    {
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSingleton(_ => new HomeAssistantClient(Options.Create(new HomeAssistantOptions
        {
            BaseUrl = "https://ha.example/", TokenSecretId = Guid.NewGuid().ToString(), DomainAllowlist = ["sensor", "light"]
        }), fixture.Secrets, fixture.Handler));
    });

    private static async Task AssertAudit(McpTestApplication factory, string tool, Dictionary<string, string> expected)
    {
        await using var connection = new SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Tool, State, SanitizedParameters FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(tool, reader.GetString(0));
        Assert.Equal("Succeeded", reader.GetString(1));
        var actual = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2))!;
        Assert.Equal(expected.OrderBy(pair => pair.Key), actual.OrderBy(pair => pair.Key));
        Assert.False(await reader.ReadAsync());
    }
}
