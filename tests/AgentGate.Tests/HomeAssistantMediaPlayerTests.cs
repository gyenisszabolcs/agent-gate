using System.Text.Json;
using AgentGate.Approval;

using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Policy;
using AgentGate.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantMediaPlayerTests
{
    internal static ControlPolicy Policy(string extra = "", string domainExtra = "", string ttsExtra = "") => HomeAssistantControlPolicyTests.Policy("""
        {"HomeAssistantControl":{"Domains":{"media_player":{
        "EntityAllowlist":["media_player.room","media_player.other"],
        "ControlServices":["turn_on","turn_off","media_play","media_pause","media_stop","media_next_track","media_previous_track","volume_mute","volume_set","speak"],
        "SensitiveControlServices":["speak"]
        """ + domainExtra + """
        }},"Tts":{"EngineEntityId":"tts.local"
        """ + ttsExtra + """
        }
        """ + extra + "}}");

    private static Dictionary<string, JsonElement> Args(string service, string? data = null, string? entity = null) =>
        HomeAssistantControlPolicyTests.Arguments("media_player", service, data, entity);

    [Theory]
    [InlineData("turn_on", "{}")]
    [InlineData("turn_off", "{}")]
    [InlineData("media_play", "{}")]
    [InlineData("media_pause", "{}")]
    [InlineData("media_stop", "{}")]
    [InlineData("media_next_track", "{}")]
    [InlineData("media_previous_track", "{}")]
    [InlineData("volume_mute", "{\"is_volume_muted\":true}")]
    [InlineData("volume_set", "{\"volume_level\":0.6}")]
    [InlineData("volume_set", "{\"volume_level\":0}")]
    public void ExactServicesAndInclusiveVolumeBoundary(string service, string data) => Policy().Validate(Args(service, data), RiskLevel.Control);

    [Theory]
    [InlineData("volume_set", "{\"volume_level\":0.60000000000000000000000001}")]
    [InlineData("volume_set", "{\"volume_level\":-1e-100}")]
    [InlineData("volume_set", "{}")]
    [InlineData("volume_mute", "{\"is_volume_muted\":\"true\"}")]
    [InlineData("play_media", "{\"media_content_id\":\"https://example.org\"}")]
    [InlineData("media_play", "{\"url\":\"https://example.org\"}")]
    public void InvalidMediaDataNeverPasses(string service, string data) => Assert.Throws<GateToolException>(() => Policy().Validate(Args(service, data), RiskLevel.Control));

    [Fact]
    public void ExplicitTargetsAndHardCeilingCannotBeExpanded()
    {
        Assert.Throws<OptionsValidationException>(() => Policy(domainExtra: ",\"AllowAllEntities\":true"));
        Assert.Throws<GateToolException>(() => Policy().Validate(Args("turn_on", entity: "media_player.unknown"), RiskLevel.Control));
        Assert.Throws<GateToolException>(() => Policy().Validate(Args("turn_on", entity: "media_player.*"), RiskLevel.Control));
        Assert.DoesNotContain("play_media", ControlPolicy.MaximumServices("media_player", RiskLevel.Control));
        Assert.Throws<GateToolException>(() => Policy().Validate(Args("turn_on"), RiskLevel.SensitiveControl));
    }

    [Fact]
    public void TtsTextBoundariesAndRedactionApplyOnBothRoutes()
    {
        foreach (var risk in new[] { RiskLevel.Control, RiskLevel.SensitiveControl })
        {
            Policy().Validate(Args("speak", JsonSerializer.Serialize(new { message = new string('a', 200) })), risk);
            Assert.Throws<GateToolException>(() => Policy().Validate(Args("speak", JsonSerializer.Serialize(new { message = new string('a', 201) })), risk));
            var clean = Policy().Validate(Args("speak", "{\"message\":\"password=canary\"}"), risk);
            Assert.DoesNotContain("canary", clean.Data.GetRawText());
        }
    }

    [Theory]
    [InlineData("{\"message\":\"<speak>hello</speak>\"}")]
    [InlineData("{\"message\":\"https://example.org/a\"}")]
    [InlineData("{\"message\":\"/media/song.mp3\"}")]
    [InlineData("{\"message\":\"hello\",\"engine_id\":\"tts.other\"}")]
    [InlineData("{\"message\":\"hello\",\"volume_level\":0.7}")]
    [InlineData("{\"message\":\"\"}")]
    public void TtsCannotCarryMarkupUrlsPathsOrEngineOverride(string data) => Assert.Throws<GateToolException>(() => Policy().Validate(Args("speak", data), RiskLevel.Control));

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-17T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private static ControlFixture Fixture(Clock clock)
    {
        var f = new ControlFixture(s =>
        {
            s.AddSingleton(Policy());
            s.AddSingleton<TimeProvider>(clock);
        }, ["media_player"]) { Entity = "media_player.room", Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.95 }) };
        f.Respond = async (req, ct) => { await ApplyVolume(f, req, ct); return f.Response(req); };
        return f;
    }

    private static async Task ApplyVolume(ControlFixture f, HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/api/services/media_player/volume_set") return;
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = body.RootElement.GetProperty("volume_level").Clone() });
    }

    [Theory]
    [InlineData("2026-11-17T20:59:59Z", false)]
    [InlineData("2026-11-17T21:00:00Z", true)]
    [InlineData("2026-11-18T05:59:59Z", true)]
    [InlineData("2026-11-18T06:00:00Z", false)]
    [InlineData("2026-10-25T00:30:00Z", true)]
    [InlineData("2026-10-25T01:30:00Z", true)]
    [InlineData("2026-03-29T01:00:00Z", true)]
    public async Task QuietBoundariesAndDstUseBudapestWallClock(string utc, bool quiet)
    {
        await using var f = Fixture(new Clock { Now = DateTimeOffset.Parse(utc) });
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"hello\",\"volume_level\":0.6}");
        Assert.Equal(quiet, ControlFixture.Failed(reply));
        Assert.Equal(quiet ? 0 : 2, f.Posts);
        if (quiet) Assert.Contains("ha_sensitive_control", reply.RootElement.GetRawText());
    }

    [Fact]
    public async Task ActualApprovalBindsRedactedMessageTargetEngineAndVolumeThenExecutesOnce()
    {
        var clock = new Clock { Now = DateTimeOffset.Parse("2026-11-17T22:00:00Z") };
        await using var f = Fixture(clock);
        var payloads = new List<(string Path, string Body)>();
        f.Respond = async (req, ct) =>
        {
            await ApplyVolume(f, req, ct);
            if (req.Method == HttpMethod.Post) payloads.Add((req.RequestUri!.AbsolutePath, await req.Content!.ReadAsStringAsync(ct)));
            return f.Response(req);
        };
        var spoken = new string('á', 170);
        using var reply = await f.Call("ha_sensitive_control", "media_player", "speak", JsonSerializer.Serialize(new { message = "password=canary " + spoken, volume_level = 0.4 }));
        Assert.False(ControlFixture.Failed(reply));
        Assert.Equal(0, f.Posts);
        Assert.DoesNotContain("canary", Assert.Single(f.Channel.Requests).Summary);
        var push = new AgentGate.Approval.HomeAssistant.ApprovalPushFormatter().Request(Assert.Single(f.Channel.Requests), f.Secrets.Token);
        Assert.Contains(spoken, push);
        Assert.Contains("tts.local", push);
        Assert.DoesNotContain("canary", push);
        Assert.Equal(ApprovalState.Executed, (await f.Channel.Approve()).State);
        Assert.Equal("/api/services/media_player/volume_set", payloads[0].Path);
        Assert.Contains("0.4", payloads[0].Body);
        Assert.Equal("/api/services/tts/speak", payloads[1].Path);
        using var body = JsonDocument.Parse(payloads[1].Body);
        Assert.Equal("tts.local", body.RootElement.GetProperty("entity_id").GetString());
        Assert.Equal("media_player.room", body.RootElement.GetProperty("media_player_entity_id").GetString());
        Assert.DoesNotContain("canary", payloads[1].Body);
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.App.AuditPath};Pooling=False");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT group_concat(SanitizedParameters) FROM AuditCalls";
        var audit = (string)(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain("canary", audit);
        Assert.Contains("text_length", audit);
        Assert.Contains("tts.local", Assert.Single(f.Channel.Requests).Summary);
        var handler = f.App.Services.GetServices<IApprovalOperationHandler>().Single(h => h.Tool == "ha_sensitive_control");
        var operation = await handler.PrepareAsync(Args("speak", "{\"message\":\"hello\"}"), CancellationToken.None);
        foreach (var field in operation.Parameters.Keys)
        {
            var changed = operation.Parameters.ToDictionary(p => p.Key, p => p.Value);
            changed[field] += "x";
            Assert.NotEqual(operation.ComputeHash("owner"), new PreparedOperation(operation.Tool, operation.Risk, changed).ComputeHash("owner"));
        }
    }

    [Fact]
    public async Task SharedTtsQuotaIncludesApprovedRouteAndExpiresAtSixtySeconds()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var delayed = false;
        f.Respond = async (req, ct) =>
        {
            await ApplyVolume(f, req, ct);
            if (!delayed && req.Method == HttpMethod.Post)
            {
                clock.Now = clock.Now.AddSeconds(30);
                delayed = true;
            }
            return f.Response(req);
        };
        using var pending = await f.Call("ha_sensitive_control", "media_player", "speak", "{\"message\":\"approved\",\"volume_level\":0.6}");
        for (var i = 0; i < 3; i++)
        {
            using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"hello\",\"volume_level\":0.6}");
            Assert.False(ControlFixture.Failed(reply));
        }
        Assert.Equal(ApprovalState.Failed, (await f.Channel.Approve()).State);
        Assert.Equal(6, f.Posts);
        clock.Now = clock.Now.AddSeconds(30);
        using var stillLimited = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"hello\",\"volume_level\":0.6}");
        Assert.True(ControlFixture.Failed(stillLimited));
        Assert.Equal(6, f.Posts);
        clock.Now = clock.Now.AddSeconds(30);
        using var next = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"hello\",\"volume_level\":0.6}");
        Assert.False(ControlFixture.Failed(next));
        Assert.Equal(8, f.Posts);
    }

    [Fact]
    public async Task ParallelTargetsCannotExceedSharedTtsQuota()
    {
        await using var f = Fixture(new Clock());
        f.Respond = async (req, ct) =>
        {
            await ApplyVolume(f, req, ct);
            return req.Method == HttpMethod.Post ? f.Response(req) : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("{\"entity_id\":\"" + req.RequestUri!.Segments[^1] + "\",\"state\":\"on\",\"attributes\":" + f.Attributes.GetRawText() + "}") };
        };
        var replies = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => f.Call("ha_control", "media_player", "speak", "{\"message\":\"hello\",\"volume_level\":0.6}", i % 2 == 0 ? "media_player.room" : "media_player.other")));
        try { Assert.Equal(3, replies.Count(r => !ControlFixture.Failed(r))); Assert.Equal(6, f.Posts); }
        finally { foreach (var r in replies) r.Dispose(); }
    }

    [Fact]
    public async Task KillSwitchBetweenVolumeAndSpeechPreventsSpeechAndDoesNotRetry()
    {
        await using var f = Fixture(new Clock());
        f.Respond = async (req, ct) =>
        {
            await ApplyVolume(f, req, ct);
            if (req.Method == HttpMethod.Post) await File.WriteAllTextAsync(f.App.KillSwitchPath, "{\"Enabled\":true}", ct);
            return f.Response(req);
        };
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"hello\",\"volume_level\":0.6}");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(1, f.Posts);
    }

    [Fact]
    public void MediaConfigurationFailsClosedAndCustomVolumeIsExact()
    {
        foreach (var value in new[] { "null", "-0.1", "1.000000000000000000001", "\"oops\"", "{}" })
            Assert.Throws<OptionsValidationException>(() => Policy(",\"MediaPlayerMaxVolume\":" + value));
        foreach (var tts in new[] { "{}", "{\"EngineEntityId\":\"media_player.room\"}", "{\"EngineEntityId\":\"tts.local\",\"QuietHoursStart\":\"25:00\"}", "{\"EngineEntityId\":\"tts.local\",\"TimeZone\":\"unknown\"}", "{\"EngineEntityId\":\"tts.local\",\"QuietHoursStart\":\"07:00\"}" })
            Assert.Throws<OptionsValidationException>(() => HomeAssistantControlPolicyTests.Policy("{\"HomeAssistantControl\":{\"Tts\":" + tts + "}}"));
        var policy = Policy(",\"MediaPlayerMaxVolume\":0.3", ttsExtra: ",\"FallbackVolume\":0.25");
        policy.Validate(Args("volume_set", "{\"volume_level\":0.3}"), RiskLevel.Control);
        Assert.Throws<GateToolException>(() => policy.Validate(Args("volume_set", "{\"volume_level\":0.300000000000000000001}"), RiskLevel.Control));
        var daytime = HomeAssistantControlPolicyTests.Policy("""
            {"HomeAssistantControl":{"Tts":{"QuietHoursStart":"09:00","QuietHoursEnd":"17:00","TimeZone":"UTC"}}}
            """).Media;
        Assert.False(daytime.IsQuiet(DateTimeOffset.Parse("2026-11-17T08:59:59Z")));
        Assert.True(daytime.IsQuiet(DateTimeOffset.Parse("2026-11-17T09:00:00Z")));
        Assert.False(daytime.IsQuiet(DateTimeOffset.Parse("2026-11-17T17:00:00Z")));
    }

    [Fact]
    public async Task MissingOrUnchangedHighVolumeCannotReachTts()
    {
        await using var f = Fixture(new Clock());
        f.Respond = (req, _) => Task.FromResult(f.Response(req));
        using var high = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"hello\",\"volume_level\":0.6}");
        Assert.True(ControlFixture.Failed(high));
        f.Attributes = JsonSerializer.SerializeToElement(new { });
        using var missing = await f.Call("ha_sensitive_control", "media_player", "speak", "{\"message\":\"hello\",\"volume_level\":0.6}");
        Assert.False(ControlFixture.Failed(missing));
        Assert.Equal(ApprovalState.Failed, (await f.Channel.Approve()).State);
        Assert.Equal(2, f.Posts);
        Assert.DoesNotContain("POST /api/services/tts/speak", f.Calls);
    }

    [Fact]
    public async Task CrossingQuietStartDuringTransportPreparationStopsControlAndNamesSensitiveRoute()
    {
        var clock = new Clock { Now = DateTimeOffset.Parse("2026-11-17T20:59:59Z") };
        await using var f = Fixture(clock);
        f.Respond = async (req, ct) =>
        {
            await ApplyVolume(f, req, ct);
            clock.Now = DateTimeOffset.Parse("2026-11-17T21:00:00Z");
            return f.Response(req);
        };
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"hello\",\"volume_level\":0.6}");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(1, f.Posts);
        Assert.Contains("ha_sensitive_control", reply.RootElement.GetRawText());
    }

    [Fact]
    public async Task SensitiveRejectAndExecutionKillSwitchNeverReachHa()
    {
        await using var f = Fixture(new Clock());
        using var pending = await f.Call("ha_sensitive_control", "media_player", "speak", "{\"message\":\"hello\"}");
        var notice = Assert.Single(f.Channel.Requests);
        await f.Channel.Decisions.Writer.WriteAsync(new(notice.Id, ApprovalChoice.Reject, "admin", DateTimeOffset.UtcNow));
        Assert.Equal(ApprovalState.Rejected, (await f.Channel.Outcomes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).State);
        f.Channel.Requests.Clear();
        using var pendingKill = await f.Call("ha_sensitive_control", "media_player", "speak", "{\"message\":\"hello\"}");
        await File.WriteAllTextAsync(f.App.KillSwitchPath, "{\"Enabled\":true}");
        Assert.Equal(ApprovalState.Failed, (await f.Channel.Approve()).State);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task VolumeTransportFailureDoesNotRetrySpeakOrLeakMessage()
    {
        await using var f = Fixture(new Clock());
        f.Respond = (_, _) => throw new HttpRequestException("transport-canary");
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"password=canary\",\"volume_level\":0.6}");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(1, f.Posts);
        Assert.DoesNotContain("canary", reply.RootElement.GetRawText());
    }
}
