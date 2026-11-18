using System.Security.Cryptography;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantLogsRedactionBoundaryTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\u2028")]
    public async Task FullTextRedactionMustNotExposePasswordAfterLineBoundary(string separator)
    {
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var log = "2026-11-18 20:00:00.000+01:00 ERROR (MainThread) [homeassistant.components.demo] password=" +
            separator + canary + "\nTraceback (most recent call last):\n  File demo.py, line 1\n";

        await using var fixture = new HomeAssistantLogsTests.Fixture(log);
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("structuredContent").GetProperty("redacted").GetBoolean());
        var sanitized = result.ToString();

        Assert.False(sanitized.Contains(canary, StringComparison.Ordinal),
            "A teljes naplószöveg szűrése után titok maradt a sorhatáron.");
    }

    [Fact]
    public async Task FullTextRedactionMustNotExposeWhitespaceSplitPasswordFragment()
    {
        var first = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var second = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var log = "2026-11-18 20:00:00.000+01:00 ERROR (MainThread) [homeassistant.components.demo] password=" +
            first + "\n" + second + "\nTraceback (most recent call last):\n  File demo.py, line 1\n";

        await using var fixture = new HomeAssistantLogsTests.Fixture(log);
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("structuredContent").GetProperty("redacted").GetBoolean());
        var sanitized = result.ToString();

        Assert.False(sanitized.Contains(first, StringComparison.Ordinal) ||
            sanitized.Contains(second, StringComparison.Ordinal),
            "A teljes naplószöveg szűrése után titoktöredék maradt a folytatósoron.");
    }
}
