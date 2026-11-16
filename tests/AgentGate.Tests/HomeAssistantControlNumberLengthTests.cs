using System.Text.Json;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantControlNumberLengthTests
{
    [Theory]
    [InlineData("brightness", 4095, true)]
    [InlineData("brightness", 4096, true)]
    [InlineData("brightness", 4097, false)]
    [InlineData("brightness", 100003, false)]
    [InlineData("temperature", 4095, true)]
    [InlineData("temperature", 4096, true)]
    [InlineData("temperature", 4097, false)]
    [InlineData("temperature", 100003, false)]
    [InlineData("min_temp", 4095, true)]
    [InlineData("min_temp", 4096, true)]
    [InlineData("min_temp", 4097, false)]
    [InlineData("min_temp", 100003, false)]
    [InlineData("max_temp", 4095, true)]
    [InlineData("max_temp", 4096, true)]
    [InlineData("max_temp", 4097, false)]
    [InlineData("max_temp", 100003, false)]
    public async Task CallerAndTrustedNumberTokensHaveFixedLexicalLimit(string source, int length, bool allowed)
    {
        var exponent = "1e-" + new string('9', length - 3);
        var temperature = source == "temperature" ? "20." + new string('0', length - 3) : "20";
        var min = source == "min_temp" ? exponent : "10";
        var max = source == "max_temp" ? "30." + new string('0', length - 3) : "30";
        await using var f = new ControlFixture
        {
            Entity = source == "brightness" ? "light.room" : "climate.room",
            Attributes = JsonDocument.Parse("{\"temperature_unit\":\"°C\",\"min_temp\":" + min + ",\"max_temp\":" + max + "}").RootElement.Clone()
        };
        using var reply = source == "brightness"
            ? await f.Call("ha_control", "light", "turn_on", "{\"brightness_pct\":" + exponent + "}")
            : await f.Call("ha_control", "climate", "set_temperature", "{\"temperature\":" + temperature + "}");
        Assert.True(ControlFixture.Failed(reply) == !allowed, "A szám lexikális hosszkorlátja nem érvényesült.");
        Assert.Equal(allowed ? 1 : 0, f.Posts);
        if (source == "brightness" && !allowed)
        {
            Assert.Empty(f.Calls);
            Assert.Equal(0, f.Secrets.Calls);
        }
    }
}
