using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Integrations.HomeAssistant.Control;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Fact]
    public async Task ActualImplicitTtsPushOmitsTechnicalDetailsKeptInSummary()
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
        Assert.Equal(2, message.Split('\n').Length);
        Assert.Equal("Jóváhagyás: felolvasás", body.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("tts.local", message);
        Assert.Contains("tts_max_volume", notice.Summary);
        Assert.Contains("tts_fallback_volume", notice.Summary);
        Assert.Equal(1, harness.Http.Count);
        Assert.Equal("GET /api/states/media_player.room", Assert.Single(f.Calls));
    }
}
