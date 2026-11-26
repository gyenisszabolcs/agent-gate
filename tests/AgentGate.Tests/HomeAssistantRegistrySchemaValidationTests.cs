using System.Text.Json;
using AgentGate.Approval;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Theory]
    [InlineData("{\"entity_id\":\"sensor.room\"}")]
    [InlineData("{\"patch\":{\"name\":\"Új\"}}")]
    [InlineData("{\"entity_id\":null,\"patch\":{\"name\":\"Új\"}}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"extra\":false,\"patch\":{\"name\":\"Új\"}}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"patch\":null}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"patch\":true}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"patch\":{}}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"patch\":{\"unknown\":\"Új\"}}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"patch\":{\"name\":false}}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"patch\":{\"name\":\"a\",\"name\":\"b\"}}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"patch\":{\"labels_add\":null}}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"patch\":{\"labels_add\":[false]}}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"patch\":{\"hidden_by\":\"integration\"}}")]
    public async Task RegistrySchemaProjectionDoesNotBypassCallValidation(string json)
    {
        using var registry = new HomeAssistantRegistryTests.Fixture();
        await using var app = new ChannelApp { RegistryFixture = registry };
        using var client = app.CreateClient();
        await WaitAsync(() => Assert.Single(app.Services.GetServices<IApprovalChannel>()).IsAvailable);
        using var arguments = JsonDocument.Parse(json);
        using var response = await CallMcpAsync(app, "ha_update_registry", arguments.RootElement);
        Assert.True(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(0, registry.Socket.Calls);
        Assert.Equal(0, registry.Writes);
        Assert.Equal(0, app.Http.Count);
    }

    [Fact]
    public async Task RegistryExplicitNullResetThroughMcpPreservesMissingFields()
    {
        using var registry = new HomeAssistantRegistryTests.Fixture();
        await using var app = new ChannelApp { RegistryFixture = registry };
        using var client = app.CreateClient();
        await WaitAsync(() => Assert.Single(app.Services.GetServices<IApprovalChannel>()).IsAvailable);
        using var arguments = JsonDocument.Parse("""{"entity_id":"sensor.room","patch":{"name":null}}""");
        using var pending = await CallMcpAsync(app, "ha_update_registry", arguments.RootElement);
        var result = pending.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.Equal(0, registry.Writes);
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(text[(text.LastIndexOf(' ') + 1)..], out var id));
        var (approve, _) = Codes(app.Http);
        app.Socket.Incoming.Writer.TryWrite(Event(approve));
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        await WaitAsync(() => gate.GetStatus(id)?.State == ApprovalState.Executed);
        Assert.Equal(1, registry.Writes);
        Assert.Equal(JsonValueKind.Null, registry.LastUpdate.GetProperty("name").ValueKind);
        Assert.False(registry.LastUpdate.TryGetProperty("area_id", out _));
        Assert.False(registry.LastUpdate.TryGetProperty("labels", out _));
    }
}
