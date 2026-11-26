using System.Text.Json;
using AgentGate.Tools;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantDeviceRegistryTests
{
    [Theory]
    [InlineData("denied")]
    [InlineData("disallowed")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("wrong-type")]
    [InlineData("malformed")]
    [InlineData("template-error")]
    [InlineData("catalog-error")]
    [InlineData("unknown-field")]
    public async Task EveryActiveEntityAndEveryResponseMustBeValid(string scenario)
    {
        using var f = new Fixture();
        Mutate(f, scenario);
        await Assert.ThrowsAnyAsync<Exception>(async () => await f.Prepare("""{"name_by_user":"Új"}"""));
        Assert.Equal(0, f.Writes);
    }

    private static void Mutate(Fixture f, string scenario)
    {
        switch (scenario)
        {
            case "denied": f.ActiveResponse = "[\"sensor.room\",\"sensor.denied\"]"; break;
            case "disallowed": f.ActiveResponse = "[\"sensor.room\",\"lock.door\"]"; break;
            case "empty": f.ActiveResponse = "[]"; break;
            case "duplicate": f.ActiveResponse = "[\"sensor.room\",\"sensor.room\"]"; break;
            case "wrong-type": f.ActiveResponse = "{\"entities\":[\"sensor.room\"]}"; break;
            case "malformed": f.ActiveResponse = "[\"sensor.room\","; break;
            case "template-error": f.TemplateFailure = true; break;
            case "catalog-error": f.Labels = "[]"; break;
            case "unknown-field": f.Device["future_unknown_2026_field"] = true; break;
            case "before-changed": f.Device["name_by_user"] = "Másik"; break;
            case "active-added": f.ActiveResponse = "[\"sensor.room\",\"sensor.other\",\"sensor.new\"]"; break;
            case "kill": f.KillSwitch.Enabled = true; break;
            default: throw new InvalidOperationException("Ismeretlen teszteset.");
        }
    }

    [Theory]
    [InlineData("denied", false)]
    [InlineData("disallowed", false)]
    [InlineData("template-error", false)]
    [InlineData("catalog-error", false)]
    [InlineData("before-changed", false)]
    [InlineData("active-added", false)]
    [InlineData("denied", true)]
    [InlineData("kill", true)]
    public async Task ExecutionRechecksPolicyCatalogAndPrestateEvenAfterWriteSocketAuthentication(string scenario, bool late)
    {
        using var f = new Fixture();
        var operation = await f.Prepare("""{"name_by_user":"Új"}""");
        if (!late) Mutate(f, scenario);
        else
        {
            var writeConnection = f.Socket.Calls + 3; // device-list, labels-list, írási socket.
            f.Socket.BeforeConnect = _ =>
            {
                if (f.Socket.Calls == writeConnection) Mutate(f, scenario);
                return Task.CompletedTask;
            };
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.Operation.ExecuteAsync(operation, default));
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("{\"disabled_by\":\"user\"}")]
    [InlineData("{\"name\":\"Másik\"}")]
    [InlineData("{\"labels_add\":[\"new\"]}")]
    [InlineData("{\"name_by_user\":\"a\",\"name_by_user\":\"b\"}")]
    public async Task DevicePatchRejectsUnsupportedOrDuplicateFieldsBeforeIo(string patch)
    {
        using var f = new Fixture();
        await Assert.ThrowsAsync<GateToolException>(async () => await f.Prepare(patch));
        Assert.Equal(0, f.Socket.Calls);
        Assert.Empty(f.TemplateRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentAndChildResetOnlyTheExplicitFields(bool child)
    {
        using var f = new Fixture { Device = DeviceRecord(child) };
        var operation = await f.Prepare("""{"name_by_user":null}""");
        await f.Operation.ExecuteAsync(operation, default);
        Assert.Equal(JsonValueKind.Null, f.LastUpdate.GetProperty("name_by_user").ValueKind);
        Assert.False(f.LastUpdate.TryGetProperty("area_id", out _));
        Assert.False(f.LastUpdate.TryGetProperty("labels", out _));
        Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData("{\"area_id\":\"missing\"}")]
    [InlineData("{\"labels\":[\"missing\"]}")]
    public async Task MissingAreaOrLabelNeverWrites(string patch)
    {
        using var f = new Fixture();
        await Assert.ThrowsAnyAsync<Exception>(async () => await f.Prepare(patch));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task CompositeErrorHasNoRetryOrParentFallback()
    {
        using var f = new Fixture { Device = DeviceRecord(true) };
        var operation = await f.Prepare("""{"name_by_user":"Új"}""");
        f.UpdateFailure = true;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => f.Operation.ExecuteAsync(operation, default));
        Assert.DoesNotContain(f.Secrets.Token, error.ToString());
        Assert.Equal(1, f.Writes);
        Assert.Equal("device", f.LastUpdate.GetProperty("device_id").GetString());
    }

    [Fact]
    public async Task DisabledRegistryStopsDevicePreparationBeforeIo()
    {
        using var f = new Fixture(enabled: false);
        await Assert.ThrowsAsync<GateToolException>(async () => await f.Prepare("""{"name_by_user":"Új"}"""));
        Assert.Equal(0, f.Socket.Calls);
        Assert.Empty(f.TemplateRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNullAreaAndEmptyLabelsClearOnlyThoseDeviceFields(bool child)
    {
        using var f = new Fixture { Device = DeviceRecord(child) };
        f.Device["area_id"] = "room";
        var operation = await f.Prepare("""{"area_id":null,"labels":[]}""");
        await f.Operation.ExecuteAsync(operation, default);
        Assert.Equal(JsonValueKind.Null, f.LastUpdate.GetProperty("area_id").ValueKind);
        Assert.Equal(0, f.LastUpdate.GetProperty("labels").GetArrayLength());
        Assert.False(f.LastUpdate.TryGetProperty("name_by_user", out _));
        Assert.Equal(1, f.Writes);
    }

    [Fact]
    public async Task GlobalEntityDenylistAlsoAppliesToEveryActiveDeviceEntity()
    {
        using var f = new Fixture(globallyDenied: "sensor.other");
        await Assert.ThrowsAsync<GateToolException>(async () => await f.Prepare("""{"name_by_user":"Új"}"""));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task YeelightWithEveryActiveEntityAllowedCanChangeItsDeviceArea()
    {
        using var f = new Fixture(domains: ["binary_sensor", "light"])
        {
            ActiveResponse = """["binary_sensor.yeelight_nightlight","light.yeelight_nappali","light.yeelight_nappali_led"]"""
        };
        f.Device["name_by_user"] = "Yeelight Ceilc 0x184479a5";
        var operation = await f.Prepare("""{"area_id":"room"}""");
        Assert.Equal(0, f.Writes);
        Assert.Contains("Yeelight Ceilc 0x184479a5", operation.Presentation!.RegistryLine());
        Assert.Contains("Terület: Nincs → Szoba", operation.Presentation.RegistryLine());
        await f.Operation.ExecuteAsync(operation, default);
        Assert.Equal(1, f.Writes);
        Assert.Equal("room", f.LastUpdate.GetProperty("area_id").GetString());
        Assert.False(f.LastUpdate.TryGetProperty("name_by_user", out _));
        Assert.False(f.LastUpdate.TryGetProperty("labels", out _));
    }

    [Fact]
    public async Task DeviceIdCannotInjectTemplateCode()
    {
        using var f = new Fixture();
        var arguments = Arguments("""{"name_by_user":"Új"}""");
        arguments["device_id"] = JsonSerializer.SerializeToElement("device\") }}{{ states() }}");
        await Assert.ThrowsAsync<GateToolException>(async () => await f.Operation.PrepareAsync(arguments, default));
        Assert.Equal(0, f.Socket.Calls);
        Assert.Empty(f.TemplateRequests);
    }
}
