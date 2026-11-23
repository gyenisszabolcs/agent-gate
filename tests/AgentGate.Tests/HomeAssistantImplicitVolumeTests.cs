using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Approval.HomeAssistant;
using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantImplicitVolumeTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; init; } = DateTimeOffset.Parse("2026-11-18T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ControlFixture Fixture(ControlPolicy? policy = null, TimeProvider? clock = null) => new(s =>
    {
        s.AddSingleton(policy ?? HomeAssistantMediaPlayerTests.Policy());
        s.AddSingleton(clock ?? new Clock());
    }, ["media_player"]) { Entity = "media_player.room" };

    private static ControlPolicy WithFallback(string fallback, string cap = "0.6") => HomeAssistantControlPolicyTests.Policy(
        "{\"HomeAssistantControl\":{\"Domains\":{\"media_player\":{\"EntityAllowlist\":[\"media_player.room\"],\"ControlServices\":[\"speak\"],\"SensitiveControlServices\":[\"speak\"]}},\"MediaPlayerMaxVolume\":" + cap +
        ",\"Tts\":{\"EngineEntityId\":\"tts.local\",\"FallbackVolume\":" + fallback + "}}}");

    [Fact]
    public void FallbackConfigurationIsPositiveExactAndCannotExceedCap()
    {
        foreach (var value in new[] { "0", "-0.1", "0.60000000000000000001", "null", "\"bad\"", "{}" })
            Assert.Throws<OptionsValidationException>(() => WithFallback(value));
        WithFallback("0.25", "0.3").Validate(HomeAssistantControlPolicyTests.Arguments("media_player", "speak", "{\"message\":\"Teszt\"}"), RiskLevel.Control);
        Assert.Throws<OptionsValidationException>(() => HomeAssistantMediaPlayerTests.Policy(",\"MediaPlayerMaxVolume\":0.3"));
        var inactive = HomeAssistantControlPolicyTests.Policy("{\"HomeAssistantControl\":{\"MediaPlayerMaxVolume\":0.1}}");
        Assert.Null(inactive.Media.EngineEntityId);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.6)]
    public async Task MissingVolumeKeepsFreshAudibleLevelWithoutVolumePost(double volume)
    {
        await using var f = Fixture();
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = volume, is_volume_muted = false });
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.False(ControlFixture.Failed(reply));
        Assert.Equal(new[] { "GET /api/states/media_player.room", "POST /api/services/tts/speak" }, f.Calls.ToArray());
        Assert.Equal(volume, reply.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("volume_level").GetDouble());
    }

    [Fact]
    public void NormalizationPreservesMissingVolumeAsDistinctIntent()
    {
        var request = HomeAssistantMediaPlayerTests.Policy().Validate(
            HomeAssistantControlPolicyTests.Arguments("media_player", "speak", "{\"message\":\"Teszt\"}"), RiskLevel.Control);
        Assert.False(request.Data.TryGetProperty("volume_level", out _));
    }

    [Theory]
    [InlineData(true, 0.2)]
    [InlineData(true, 0.95)]
    [InlineData(false, 0.0)]
    public async Task MutedOrZeroRestoresDefaultAndConfirmsBeforeSpeech(bool muted, double level)
    {
        await using var f = Fixture();
        var initiallyMuted = muted;
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = level, is_volume_muted = muted });
        f.Respond = async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("media_player/", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
                if (body.RootElement.TryGetProperty("is_volume_muted", out var m)) muted = m.GetBoolean();
                if (body.RootElement.TryGetProperty("volume_level", out var v)) level = v.GetDouble();
                f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = level, is_volume_muted = muted });
            }
            return f.Response(req);
        };
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.False(ControlFixture.Failed(reply));
        var expected = new List<string> { "GET /api/states/media_player.room", "POST /api/services/media_player/volume_set",
            "GET /api/states/media_player.room" };
        if (initiallyMuted) expected.AddRange(["POST /api/services/media_player/volume_mute", "GET /api/states/media_player.room"]);
        expected.Add("POST /api/services/tts/speak");
        Assert.Equal(expected, f.Calls.ToArray());
        Assert.False(muted);
        Assert.Equal(0.4, level);
    }

    [Fact]
    public async Task ImplicitApprovalDisplaysBoundRecoveryAndExecutesConfiguredFallbackOnce()
    {
        await using var f = Fixture(WithFallback("0.25", "0.3"), new Clock { Now = DateTimeOffset.Parse("2026-11-18T22:00:00Z") });
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.0, is_volume_muted = true });
        f.Respond = async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/volume_set", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
                Assert.Equal(0.25, body.RootElement.GetProperty("volume_level").GetDouble());
                f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.25, is_volume_muted = true });
            }
            if (req.RequestUri!.AbsolutePath.EndsWith("/volume_mute", StringComparison.Ordinal))
                f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.25, is_volume_muted = false });
            return f.Response(req);
        };
        using var quiet = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.True(ControlFixture.Failed(quiet));
        Assert.Contains("ha_sensitive_control", quiet.RootElement.GetRawText());
        using var pending = await f.Call("ha_sensitive_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.False(ControlFixture.Failed(pending));
        var notice = Assert.Single(f.Channel.Requests);
        var push = new ApprovalPushFormatter().Request(notice, f.Secrets.Token);
        Assert.Contains("némításfeloldás", push);
        Assert.Contains("0.25", push);
        Assert.Contains("0.3", push);
        Assert.Contains("bekapcsolás", push);
        Assert.Contains("nincs kifejezett", push);
        Assert.Empty(f.Calls);
        Assert.Equal(ApprovalState.Executed, (await f.Channel.Approve()).State);
        Assert.Equal(3, f.Posts);

        var handler = f.App.Services.GetServices<IApprovalOperationHandler>().Single(h => h.Tool == "ha_sensitive_control");
        var op = await handler.PrepareAsync(HomeAssistantControlPolicyTests.Arguments("media_player", "speak", "{\"message\":\"Teszt\"}"), CancellationToken.None);
        Assert.Equal("0.25", op.Parameters["tts_fallback_volume"]);
        Assert.Equal("0.3", op.Parameters["tts_max_volume"]);
        foreach (var field in new[] { "tts_fallback_volume", "tts_max_volume" })
        {
            var changed = op.Parameters.ToDictionary(p => p.Key, p => p.Value);
            changed[field] = "0.2";
            var tampered = new PreparedOperation(op.Tool, op.Risk, changed);
            Assert.NotEqual(op.ComputeHash("owner"), tampered.ComputeHash("owner"));
            await Assert.ThrowsAnyAsync<Exception>(() => handler.ExecuteAsync(tampered, CancellationToken.None));
        }
        var replacedIntent = op.Parameters.ToDictionary(p => p.Key, p => p.Value);
        replacedIntent["data"] = "{\"message\":\"Teszt\",\"volume_level\":0.25}";
        var changedIntent = new PreparedOperation(op.Tool, op.Risk, replacedIntent);
        Assert.NotEqual(op.ComputeHash("owner"), changedIntent.ComputeHash("owner"));
        await Assert.ThrowsAnyAsync<Exception>(() => handler.ExecuteAsync(changedIntent, CancellationToken.None));
        Assert.Equal(3, f.Posts);
    }

    [Theory]
    [InlineData("off", "{\"volume_level\":0.2,\"is_volume_muted\":false}", "turn_on", 0.2)]
    [InlineData("on", "{}", "turn_on", 0.2)]
    [InlineData("on", "{\"volume_level\":0.9,\"is_volume_muted\":false}", "volume_set", 0.6)]
    public async Task ActivationOrLoweringIsConfirmedWithoutRepeatingPosts(string state, string attributes, string service, double expected)
    {
        await using var f = Fixture();
        var applied = false;
        f.Respond = (req, _) =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.EndsWith("/" + service, StringComparison.Ordinal)) applied = true;
            return Task.FromResult(req.Method == HttpMethod.Get
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    entity_id = f.Entity, state = applied ? "on" : state,
                    attributes = applied ? JsonSerializer.SerializeToElement(new { volume_level = expected, is_volume_muted = false }) : JsonDocument.Parse(attributes).RootElement.Clone()
                })) } : f.Response(req));
        };
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.False(ControlFixture.Failed(reply));
        Assert.Equal(new[] { "GET /api/states/media_player.room", "POST /api/services/media_player/" + service,
            "GET /api/states/media_player.room", "POST /api/services/tts/speak" }, f.Calls.ToArray());
    }

    [Theory]
    [InlineData("{\"volume_level\":0.1,\"is_volume_muted\":true}")]
    [InlineData("{\"volume_level\":0,\"is_volume_muted\":false}")]
    [InlineData("{\"volume_level\":0.9,\"is_volume_muted\":false}")]
    [InlineData("{}")]
    public Task UnconfirmedRecoveryNeverSpeaksOrRetriesEffect(string attributes) => AssertUnconfirmedRecoveryAsync(attributes);

    [Fact]
    public Task FallbackVolumeWithoutUnmuteAcknowledgementCannotSpeak() =>
        AssertUnconfirmedRecoveryAsync("{\"volume_level\":0.4,\"is_volume_muted\":true}");

    private static async Task AssertUnconfirmedRecoveryAsync(string attributes)
    {
        await using var f = Fixture();
        using var json = JsonDocument.Parse(attributes);
        f.Attributes = json.RootElement.Clone();
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Contains("3 másodperc", reply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.DoesNotContain("POST /api/services/tts/speak", f.Calls);
        var posts = f.Calls.Where(c => c.StartsWith("POST", StringComparison.Ordinal)).ToArray();
        Assert.Equal(posts.Length, posts.Distinct(StringComparer.Ordinal).Count());
        Assert.InRange(f.Calls.Count(c => c.StartsWith("GET", StringComparison.Ordinal)), 1, 6);
    }

    [Fact]
    public async Task ImplicitQuotaCountsLogicalSpeechRatherThanRecoveryPosts()
    {
        await using var f = Fixture();
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.25, is_volume_muted = false });
        for (var i = 0; i < 3; i++)
        {
            using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
            Assert.False(ControlFixture.Failed(reply));
        }
        using var limited = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.True(ControlFixture.Failed(limited));
        Assert.Equal(3, f.Posts);
        Assert.Equal(3, f.Calls.Count(c => c.StartsWith("GET", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task KillSwitchAfterFallbackStopsUnmuteAndSpeech()
    {
        await using var f = Fixture();
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0, is_volume_muted = true });
        f.Respond = async (req, ct) =>
        {
            if (req.Method == HttpMethod.Post) await File.WriteAllTextAsync(f.App.KillSwitchPath, "{\"Enabled\":true}", ct);
            return f.Response(req);
        };
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(new[] { "GET /api/states/media_player.room", "POST /api/services/media_player/volume_set" }, f.Calls.ToArray());
    }

    [Fact]
    public async Task CallerCannotProvideInternalRecoveryFields()
    {
        await using var f = Fixture();
        using var reply = await f.Call("ha_sensitive_control", "media_player", "speak", "{\"message\":\"Teszt\",\"tts_fallback_volume\":0.2}");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Empty(f.Channel.Requests);
        Assert.Empty(f.Calls);
    }

    [Theory]
    [InlineData("{\"volume_level\":0.2}")]
    [InlineData("{\"volume_level\":0.2,\"is_volume_muted\":\"false\"}")]
    [InlineData("{\"volume_level\":0.2,\"volume_level\":0.3,\"is_volume_muted\":false}")]
    public async Task AmbiguousAudibilityFailsWithFixedVolumeSafetyReason(string attributes)
    {
        await using var f = Fixture();
        using var json = JsonDocument.Parse(attributes);
        f.Attributes = json.RootElement.Clone();
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Contains("biztonságos hangerő nem igazolható", reply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task ExplicitMutedZeroIsNotReplacedByFallback()
    {
        await using var f = Fixture();
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0, is_volume_muted = true });
        f.Respond = async (req, ct) =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/volume_set", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
                Assert.Equal(0, body.RootElement.GetProperty("volume_level").GetDouble());
            }
            return f.Response(req);
        };
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\",\"volume_level\":0}");
        Assert.False(ControlFixture.Failed(reply));
        Assert.Equal(new[] { "POST /api/services/media_player/volume_set", "GET /api/states/media_player.room", "POST /api/services/tts/speak" }, f.Calls.ToArray());
    }

    [Fact]
    public async Task UnavailableSpeakerAttemptsActivationOnceButNeverSpeaks()
    {
        await using var f = Fixture();
        f.Respond = (req, _) => Task.FromResult(req.Method == HttpMethod.Get
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"entity_id\":\"media_player.room\",\"state\":\"unavailable\",\"attributes\":{}}") }
            : f.Response(req));
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(1, f.Posts);
        Assert.Contains("POST /api/services/media_player/turn_on", f.Calls);
        Assert.DoesNotContain("POST /api/services/tts/speak", f.Calls);
    }

    [Fact]
    public async Task FailedLoweringIsNotRetriedAndDoesNotLeakProviderError()
    {
        await using var f = Fixture();
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.9, is_volume_muted = false });
        f.Respond = (req, _) => Task.FromResult(req.Method == HttpMethod.Post
            ? new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("provider-canary") }
            : f.Response(req));
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        Assert.True(ControlFixture.Failed(reply));
        var text = reply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("elutasította a hangerőállítást", text);
        Assert.Contains("nincs visszagörgetés vagy újrapróbálás", text);
        Assert.DoesNotContain("provider-canary", text);
        Assert.Equal(new[] { "GET /api/states/media_player.room", "POST /api/services/media_player/volume_set" }, f.Calls.ToArray());
    }
}
