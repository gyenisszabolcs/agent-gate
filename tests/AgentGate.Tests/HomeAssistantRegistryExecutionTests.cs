using System.Text.Json;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantRegistryExecutionTests
{
    [Fact]
    public async Task KillSwitchEnabledDuringFinalReadPreventsTheUpdate()
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"name\":\"Új\"}"), default);
        f.Socket.BeforeReceive = (frame, _) =>
        {
            if (frame == 2 && f.Socket.Calls == 4) f.KillSwitch.Enabled = true;
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Operation.ExecuteAsync(op, default));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task CancellationBeforePreparationNeverConnects()
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Operation.PrepareAsync(
            HomeAssistantRegistryTests.Arguments("{\"name\":\"Új\"}"), cancellation.Token).AsTask());
        Assert.Equal(0, f.Socket.Calls);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task SupportedFieldsUseOnlyTheFixedEntityUpdateCommand()
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments(
            "{\"area_id\":\"room\",\"labels_add\":[\"new\"],\"labels_remove\":[\"keep\"],\"name\":\"Új\",\"icon\":\"mdi:thermometer\",\"hidden_by\":\"user\",\"disabled_by\":\"user\"}"), default);
        await f.Operation.ExecuteAsync(op, default);
        Assert.Equal(1, f.Writes);
        Assert.Equal("config/entity_registry/update", f.LastUpdate.GetProperty("type").GetString());
        Assert.Equal("room", f.LastUpdate.GetProperty("area_id").GetString());
        Assert.Equal("mdi:thermometer", f.LastUpdate.GetProperty("icon").GetString());
        Assert.Equal("user", f.LastUpdate.GetProperty("hidden_by").GetString());
        Assert.Equal("user", f.LastUpdate.GetProperty("disabled_by").GetString());
        Assert.Equal("[\"new\"]", JsonSerializer.Serialize(f.LastUpdate.GetProperty("labels")));
        Assert.False(f.LastUpdate.TryGetProperty("new_entity_id", out _));
    }

    [Fact]
    public async Task ChangeDuringWriteAuthenticationCannotWriteStaleApproval()
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"name\":\"Új\"}"), default);
        f.Socket.BeforeReceive = (frame, _) =>
        {
            if (frame == 1 && f.Socket.Calls == 4) f.Name = "Másik név";
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Operation.ExecuteAsync(op, default));
        Assert.Equal(0, f.Writes);
    }
}
