using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.Registry;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantRegistryInheritedAreaTests
{
    [Fact]
    public async Task InheritedAreaIsShownAndBoundInServerSnapshot()
    {
        using var f = Fixture();
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("""{"area_id":"living"}"""), default);
        Assert.Equal("Hálószoba (öröklött)", op.Presentation!.Changes["area_id"].Before);
        Assert.Equal("Nappali (saját)", op.Presentation.Changes["area_id"].After);
        using var before = JsonDocument.Parse(op.Parameters["before"]!);
        Assert.Equal("bedroom", before.RootElement.GetProperty("device_area_id").GetString());
        Assert.Equal("bedroom", before.RootElement.GetProperty("effective_area_id").GetString());
        Assert.Equal("device", before.RootElement.GetProperty("area_source").GetString());
        Assert.Equal(JsonValueKind.Null, before.RootElement.GetProperty("area_id").ValueKind);
        Assert.True(f.DeviceReads > 0);
        await f.Operation.ExecuteAsync(op, default);
        Assert.Equal(1, f.Writes);
        Assert.Equal("living", f.LastUpdate.GetProperty("area_id").GetString());
        Assert.False(f.LastUpdate.TryGetProperty("device_area_id", out _));
        Assert.Equal("bedroom", f.DeviceArea);
    }

    [Fact]
    public async Task MovingInheritedDeviceAreaRejectsPreviouslyPreparedOperation()
    {
        using var f = Fixture();
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("""{"area_id":"living"}"""), default);
        f.DeviceArea = "living";
        await Assert.ThrowsAnyAsync<Exception>(() => f.Operation.ExecuteAsync(op, default));
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("own")]
    [InlineData("none")]
    [InlineData("clear")]
    [InlineData("missing")]
    [InlineData("same")]
    public async Task OwnInheritedAndMissingAreaHaveDistinctSemantics(string scenario)
    {
        using var f = Fixture();
        if (scenario is "own" or "clear" or "missing") f.Area = "living";
        if (scenario == "none") f.Device = null;
        var patch = scenario switch
        {
            "clear" => """{"area_id":null}""",
            "missing" => """{"name":"Új"}""",
            "same" => """{"area_id":"bedroom"}""",
            _ => """{"area_id":"bedroom"}"""
        };
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments(patch), default);
        using var after = JsonDocument.Parse(op.Parameters["after"]!);
        Assert.Equal(scenario == "missing" ? "living" : "bedroom", after.RootElement.GetProperty("effective_area_id").GetString());
        Assert.Equal(scenario == "clear" ? "device" : "entity", after.RootElement.GetProperty("area_source").GetString());
        if (scenario != "missing")
        {
            Assert.Equal(scenario switch
            {
                "own" or "clear" => "Nappali (saját)",
                "none" => "Nincs",
                _ => "Hálószoba (öröklött)"
            }, op.Presentation!.Changes["area_id"].Before);
            Assert.Equal(scenario == "clear" ? "Hálószoba (öröklött)" : "Hálószoba (saját)", op.Presentation!.Changes["area_id"].After);
        }
        await f.Operation.ExecuteAsync(op, default);
        Assert.Equal(1, f.Writes);
        if (scenario == "missing") Assert.False(f.LastUpdate.TryGetProperty("area_id", out _));
        if (scenario == "clear") Assert.Equal(JsonValueKind.Null, f.LastUpdate.GetProperty("area_id").ValueKind);
        if (scenario == "none") Assert.Equal(0, f.DeviceReads);
    }

    [Theory]
    [InlineData("own", false)]
    [InlineData("device-area", false)]
    [InlineData("link", false)]
    [InlineData("own", true)]
    [InlineData("device-area", true)]
    [InlineData("link", true)]
    public async Task StateChangeAtEitherExecutionCheckNeverWrites(string change, bool duringAuthentication)
    {
        using var f = Fixture();
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("""{"area_id":"living"}"""), default);
        void Change()
        {
            if (change == "own") f.Area = "living";
            if (change == "device-area") f.DeviceArea = null;
            if (change == "link") f.Device = null;
        }
        if (duringAuthentication) MutateDuringWriteAuthentication(f, Change);
        else Change();
        var error = await Assert.ThrowsAsync<RegistryPrestateChangedException>(() => f.Operation.ExecuteAsync(op, default));
        Assert.Equal(RegistryPrestateChangedException.SafeMessage, error.Message);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task DeviceAreaIsHashBoundEvenWhenOwnAreaOverridesIt()
    {
        using var f = Fixture();
        f.Area = "living";
        var args = HomeAssistantRegistryTests.Arguments("""{"name":"Új"}""");
        var old = await f.Operation.PrepareAsync(args, default);
        f.DeviceArea = null;
        var changed = await f.Operation.PrepareAsync(args, default);
        Assert.NotEqual(old.ComputeHash("Mikaela"), changed.ComputeHash("Mikaela"));
        await Assert.ThrowsAsync<RegistryPrestateChangedException>(() => f.Operation.ExecuteAsync(old, default));
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"id\":\"device\"}]")]
    [InlineData("[{\"id\":\"device\",\"area_id\":false}]")]
    [InlineData("[{\"id\":42,\"area_id\":null}]")]
    [InlineData("[{\"id\":\"device\",\"area_id\":null},{\"id\":\"device\",\"area_id\":\"bedroom\"}]")]
    [InlineData("[{\"id\":\"device\",\"area_id\":null,\"area_id\":\"bedroom\"}]")]
    public async Task UncertainDeviceCatalogFailsClosed(string catalog)
    {
        using var f = Fixture();
        f.DevicesCatalog = catalog;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("""{"area_id":"living"}"""), default).AsTask());
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"id\":\"device\",\"area_id\":17}]")]
    public async Task InvalidDeviceCatalogAfterWriteAuthenticationNeverWrites(string catalog)
    {
        using var f = Fixture();
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("""{"area_id":"living"}"""), default);
        MutateDuringWriteAuthentication(f, () => f.DevicesCatalog = catalog);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Operation.ExecuteAsync(op, default));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task EmptyDeviceAreaAndOwnAreaResolveToNone()
    {
        using var f = Fixture();
        f.DeviceArea = null;
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("""{"area_id":"living"}"""), default);
        using var before = JsonDocument.Parse(op.Parameters["before"]!);
        Assert.Equal("none", before.RootElement.GetProperty("area_source").GetString());
        Assert.Equal(JsonValueKind.Null, before.RootElement.GetProperty("effective_area_id").ValueKind);
        Assert.Equal("Nincs", op.Presentation!.Changes["area_id"].Before);
        await f.Operation.ExecuteAsync(op, default);
        Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("17")]
    [InlineData("\"\"")]
    public async Task MalformedEntityDeviceAssociationFailsClosed(string device)
    {
        using var f = Fixture();
        var original = f.Socket.RequestBody!;
        f.Socket.RequestBody = command =>
        {
            var response = original(command);
            return command.GetProperty("type").GetString() == "config/entity_registry/get"
                ? response.Replace("\"device_id\":\"device\"", "\"device_id\":" + device, StringComparison.Ordinal)
                : response;
        };
        await Assert.ThrowsAnyAsync<Exception>(() => f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("""{"area_id":"living"}"""), default).AsTask());
        Assert.Equal(0, f.DeviceReads);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task ArbitraryAuthenticationErrorIsNotMappedToStaleOrLeaked()
    {
        using var f = Fixture();
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("""{"area_id":"living"}"""), default);
        MutateDuringWriteAuthentication(f, () => throw new InvalidOperationException("HA-private-error-canary"));
        var error = await Assert.ThrowsAsync<AgentGate.Integrations.HomeAssistant.HomeAssistantException>(() => f.Operation.ExecuteAsync(op, default));
        Assert.DoesNotContain("HA-private-error-canary", error.ToString());
        Assert.Null(new RegistryCallPolicy().GetSafeMessage(error, op));
        Assert.Equal(0, f.Writes);
    }

    internal static void MutateDuringWriteAuthentication(HomeAssistantRegistryTests.Fixture f, Action change)
    {
        // Újra-prepare: entity get, device list, area list; utána nyílik az írási socket.
        var writeSocket = f.Socket.Calls + 4;
        f.Socket.BeforeReceive = (frame, _) =>
        {
            if (frame == 1 && f.Socket.Calls == writeSocket) change();
            return Task.CompletedTask;
        };
    }

    internal static HomeAssistantRegistryTests.Fixture Fixture() => new()
    {
        Device = "device",
        AreasCatalog = """[{"area_id":"bedroom","name":"Hálószoba"},{"area_id":"living","name":"Nappali"}]"""
    };
}
