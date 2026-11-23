using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Integrations.HomeAssistant.Control;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Fact]
    public async Task ActualImplicitTtsPushCarriesRecoveryIntentAndExactConfiguredVolumes()
    {
        await using var f = new ControlFixture(s => s.AddSingleton(HomeAssistantMediaPlayerTests.Policy()), ["media_player"])
        { Entity = "media_player.room" };
        using var pending = await f.Call("ha_sensitive_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.False(ControlFixture.Failed(pending));
        var notice = Assert.Single(f.Channel.Requests);
        await using var harness = new Standalone();
        await harness.StartAsync();
        await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("nincs kifejezett hangerőkérés", message);
        Assert.Contains("bekapcsolás", message);
        Assert.Contains("plafonra csökkentés: 0.6", message);
        Assert.Contains("némításfeloldás és alap-hangerő: 0.4", message);
        Assert.Contains("tts.local", message);
        Assert.Equal(1, harness.Http.Count);
        Assert.Empty(f.Calls);
    }
}
