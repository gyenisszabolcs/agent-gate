using System.Security.Cryptography;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantLogsRedactionBoundaryTests
{
    [Theory]
    [InlineData("private_key=\n")]
    [InlineData("private_key \t: \n")]
    [InlineData("\"private_key\":\n")]
    [InlineData("'private_key' =\n")]
    public async Task NamedKeyAssignmentWithContinuationWithholdsOnlySensitiveEntry(string assignment)
    {
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        const string safe = "keyboard=ready; KeyError: keyboard; monkey=ready";
        await using var fixture = new HomeAssistantLogsTests.Fixture(
            HomeAssistantLogsTests.Entry(1, assignment + canary) + HomeAssistantLogsTests.Entry(2, safe));
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.False(result.ToString().Contains(canary, StringComparison.Ordinal),
            "Névrészes key-hozzárendelés folytatósori titka maradt az MCP-válaszban.");
        var data = result.GetProperty("structuredContent");
        Assert.Equal(1, data.GetProperty("withheld_count").GetInt32());
        Assert.Equal(1, data.GetProperty("returned_count").GetInt32());
        Assert.True(data.GetProperty("redacted").GetBoolean());
        Assert.Equal("2: " + safe, Assert.Single(data.GetProperty("items").EnumerateArray()).GetProperty("messages")[0].GetString());
        Assert.Equal(data.GetRawText(), result.GetProperty("content")[0].GetProperty("text").GetString());
        await fixture.AssertNoRawData(canary);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\u2028")]
    public async Task FullTextRedactionMustNotExposePasswordAfterLineBoundary(string separator)
    {
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var log = "2026-11-18 20:00:00.000+01:00 ERROR (MainThread) [homeassistant.components.demo] password=" +
            separator + canary + "\nTraceback (most recent call last):\n  File demo.py, line 1\n";

        await using var fixture = new HomeAssistantLogsTests.Fixture(HomeAssistantLogsTests.Entry(1, log));
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

        await using var fixture = new HomeAssistantLogsTests.Fixture(HomeAssistantLogsTests.Entry(1, log));
        using var call = await fixture.Call(new { });
        var result = call.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("structuredContent").GetProperty("redacted").GetBoolean());
        var sanitized = result.ToString();

        Assert.False(sanitized.Contains(first, StringComparison.Ordinal) ||
            sanitized.Contains(second, StringComparison.Ordinal),
            "A teljes naplószöveg szűrése után titoktöredék maradt a folytatósoron.");
    }
}
