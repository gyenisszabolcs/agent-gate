using System.Text.Json;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantControlTemperatureTests
{
    [Theory]
    [InlineData("{\"temperature_unit\":\"°F\",\"temperature_unit\":\"°C\",\"min_temp\":10,\"max_temp\":30}", 20, false, false)]
    [InlineData("{\"temperature_unit\":\"°C\",\"min_temp\":25,\"min_temp\":10,\"max_temp\":30}", 20, false, false)]
    [InlineData("{\"temperature_unit\":\"°C\",\"min_temp\":16,\"max_temp\":26}", 16, true, false)]
    [InlineData("{\"unit_of_measurement\":\"°C\",\"min_temp\":16,\"max_temp\":26}", 26, true, false)]
    [InlineData("{\"temperature_unit\":\"°C\",\"unit_of_measurement\":\"°C\",\"min_temp\":16,\"max_temp\":26}", 20, true, false)]
    [InlineData("{\"min_temp\":0,\"max_temp\":50}", 10, true, true)]
    [InlineData("{\"min_temp\":0,\"max_temp\":50}", 30, true, true)]
    [InlineData("{\"min_temp\":20,\"max_temp\":20}", 20, true, true)]
    [InlineData("{\"min_temp\":30,\"max_temp\":35}", 30, true, true)]
    [InlineData("{\"min_temp\":5,\"max_temp\":10}", 10, true, true)]
    [InlineData("{\"temperature_unit\":\"°F\",\"min_temp\":0,\"max_temp\":50}", 20, false, false)]
    [InlineData("{\"unit_of_measurement\":\"K\",\"min_temp\":0,\"max_temp\":50}", 20, false, false)]
    [InlineData("{\"temperature_unit\":\"°C\",\"unit_of_measurement\":\"°F\",\"min_temp\":0,\"max_temp\":50}", 20, false, false)]
    [InlineData("{\"temperature_unit\":null,\"min_temp\":0,\"max_temp\":50}", 20, false, false)]
    [InlineData("{\"temperature_unit\":7,\"min_temp\":0,\"max_temp\":50}", 20, false, false)]
    [InlineData("{\"temperature_unit\":\"C\",\"min_temp\":0,\"max_temp\":50}", 20, false, false)]
    [InlineData("{\"min_temp\":21,\"max_temp\":26}", 20, false, true)]
    [InlineData("{\"min_temp\":10,\"max_temp\":19}", 20, false, true)]
    [InlineData("{\"min_temp\":31,\"max_temp\":35}", 30, false, true)]
    [InlineData("{\"min_temp\":0,\"max_temp\":9}", 10, false, true)]
    [InlineData("{\"min_temp\":25,\"max_temp\":15}", 20, false, false)]
    [InlineData("{\"min_temp\":null,\"max_temp\":30}", 20, false, false)]
    [InlineData("{\"min_temp\":\"10\",\"max_temp\":30}", 20, false, false)]
    [InlineData("{\"min_temp\":10,\"max_temp\":\"30\"}", 20, false, false)]
    [InlineData("{\"min_temp\":10}", 20, false, false)]
    [InlineData("{\"max_temp\":30}", 20, false, false)]
    [InlineData("{}", 20, false, false)]
    [InlineData("{\"min_temp\":1e400,\"max_temp\":30}", 20, false, false)]
    [InlineData("{\"min_temp\":10,\"max_temp\":1e400}", 20, false, false)]
    public async Task CelsiusProofAndFreshBoundsAreRequiredWithoutConversion(string attributes, double temperature, bool allowed, bool configRead)
    {
        await using var f = new ControlFixture { Entity = "climate.room", Attributes = JsonDocument.Parse(attributes).RootElement.Clone() };
        using var reply = await f.Call("ha_control", "climate", "set_temperature", JsonSerializer.Serialize(new { temperature }));
        Assert.Equal(!allowed, ControlFixture.Failed(reply));
        Assert.Equal(allowed ? 1 : 0, f.Posts);
        Assert.Equal(configRead ? 1 : 0, f.Calls.Count(c => c == "GET /api/config"));
    }

    [Theory]
    [InlineData("20.000000000000000000000000000001", "21", "20")]
    [InlineData("19", "19.999999999999999999999999999999", "20")]
    [InlineData("20.000000000000000000000000000001", "20", "20")]
    public async Task ExactTrustedBoundsCannotBeRoundedIntoPermission(string min, string max, string requested)
    {
        await using var f = new ControlFixture
        {
            Entity = "climate.room",
            Attributes = JsonDocument.Parse("{\"temperature_unit\":\"°C\",\"min_temp\":" + min + ",\"max_temp\":" + max + "}").RootElement.Clone()
        };
        using var reply = await f.Call("ha_control", "climate", "set_temperature", "{\"temperature\":" + requested + "}");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(0, f.Posts);
    }

    [Theory]
    [InlineData("{\"unit_system\":{\"temperature\":\"°F\",\"temperature\":\"°C\"}}")]
    [InlineData("{\"unit_system\":{\"temperature\":\"°F\"},\"unit_system\":{\"temperature\":\"°C\"}}")]
    [InlineData("{}")]
    [InlineData("{\"unit_system\":null}")]
    [InlineData("{\"unit_system\":{}}")]
    [InlineData("{\"unit_system\":{\"temperature\":\"°F\"}}")]
    [InlineData("{\"unit_system\":{\"temperature\":null}}")]
    [InlineData("{\"unit_system\":{\"temperature\":12}}")]
    public async Task AbsentEntityUnitCannotUseUnprovenGlobalUnit(string config)
    {
        await using var f = new ControlFixture
        {
            Entity = "climate.room",
            Attributes = JsonSerializer.SerializeToElement(new { min_temp = 10, max_temp = 30 }),
            Config = JsonDocument.Parse(config).RootElement.Clone()
        };
        using var reply = await f.Call("ha_control", "climate", "set_temperature", "{\"temperature\":20}");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(0, f.Posts);
    }
}
