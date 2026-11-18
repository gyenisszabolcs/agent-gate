using System.Security.Cryptography;
using AgentGate.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantControlDescriptionTests
{
    [Theory]
    [InlineData("ha_control", RiskLevel.Control)]
    [InlineData("ha_sensitive_control", RiskLevel.SensitiveControl)]
    public async Task ToolsListExplainsGateDecidedTtsRoutingAndPreservesContract(string name, RiskLevel risk)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var app = new McpTestApplication(token, services => services.AddLogging(logging => logging.ClearProviders()));
        using var client = HomeAssistantToolProtocolTests.CreateClient(app, token);
        using var list = await HomeAssistantToolProtocolTests.SendAsync(client, "tools/list", new { });
        var tool = Assert.Single(list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
            item => item.GetProperty("name").GetString() == name);

        var annotations = tool.GetProperty("annotations");
        Assert.False(annotations.GetProperty("readOnlyHint").GetBoolean());
        Assert.True(annotations.GetProperty("destructiveHint").GetBoolean());
        Assert.False(annotations.GetProperty("openWorldHint").GetBoolean());
        var registered = Assert.Single(app.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!,
            item => item.ProtocolTool.Name == name);
        Assert.Equal(risk, Assert.Single(registered.Metadata.OfType<ToolRiskAttribute>()).Level);

        var schema = tool.GetProperty("inputSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");
        Assert.Equal(new[] { "data", "domain", "entity_id", "service" }, properties.EnumerateObject().Select(property => property.Name).Order());
        foreach (var parameter in new[] { "domain", "service", "entity_id" })
            Assert.Equal("string", properties.GetProperty(parameter).GetProperty("type").GetString());
        Assert.Equal(new[] { "domain", "entity_id", "service" }, schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).Order());

        var description = tool.GetProperty("description").GetString();
        Assert.NotNull(description);
        Assert.Contains("media_player speak felolvasás", description, StringComparison.Ordinal);
        Assert.Contains("csendes órákon kívül a ha_control", description, StringComparison.Ordinal);
        Assert.Contains("Felolvasásra a ha_sensitive_control csak akkor", description, StringComparison.Ordinal);
        Assert.Contains("a gate a csendes órákra hivatkozva elutasította a ha_control kérést", description, StringComparison.Ordinal);
        Assert.Contains("Az időablakról és a jogosultságról a gate dönt, nem az agent saját órája", description, StringComparison.Ordinal);
        Assert.Contains("Policy-, entitás-, hangerő-, szöveg-, kvóta- vagy hálózati hiba esetén ne válts sensitive útra", description, StringComparison.Ordinal);
        if (name == "ha_sensitive_control")
            Assert.Contains("Engedélyezett zár és érzékeny cover vezérlése a gate jóváhagyásával", description, StringComparison.Ordinal);
    }
}
