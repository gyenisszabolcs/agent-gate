using System.Net;
using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Audit;
using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantTtsConfirmationTests
{
    [Fact]
    public async Task ImplicitFallbackWaitsForMutedTargetThenConfirmsUnmutedFinalState()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var reads = new List<long>();
        var level = 0.95;
        var muted = true;
        var unmuteRequested = false;
        var unsafeUnmute = false;
        f.Respond = async (req, ct) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get)
            {
                reads.Add(clock.GetTimestamp());
                // A volume_set ACK után még egy régi állapot érkezik, majd a cél még némítva igazolódik.
                if (reads.Count >= 3) level = 0.4;
                // Az unmute ACK sem végállapot: annak alkalmazása is késik egy olvasást.
                if (unmuteRequested && reads.Count >= 5) muted = false;
            }
            if (path.EndsWith("/volume_set", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
                Assert.Equal(0.4, body.RootElement.GetProperty("volume_level").GetDouble());
            }
            if (path.EndsWith("/volume_mute", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
                Assert.False(body.RootElement.GetProperty("is_volume_muted").GetBoolean());
                unsafeUnmute = level != 0.4 || !muted || reads.Count < 3;
                unmuteRequested = true;
            }
            f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = level, is_volume_muted = muted });
            return f.Response(req);
        };
        var call = f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        for (var i = 0; i < 4; i++)
        {
            await WaitUntil(() => call.IsCompleted || clock.HasTimer(TimeSpan.FromMilliseconds(500)));
            Assert.False(call.IsCompleted);
            if (i < 2) Assert.DoesNotContain("POST /api/services/media_player/volume_mute", f.Calls);
            Assert.DoesNotContain("POST /api/services/tts/speak", f.Calls);
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }
        using var reply = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(ControlFixture.Failed(reply));
        Assert.False(unsafeUnmute);
        Assert.Equal(new long[] { 0, 5_000_000, 10_000_000, 15_000_000, 20_000_000 }, reads);
        Assert.Equal(new[] { "GET /api/states/media_player.room", "POST /api/services/media_player/volume_set",
            "GET /api/states/media_player.room", "GET /api/states/media_player.room", "POST /api/services/media_player/volume_mute",
            "GET /api/states/media_player.room", "GET /api/states/media_player.room", "POST /api/services/tts/speak" }, f.Calls.ToArray());
    }

    [Fact]
    public async Task ImplicitFallbackNeverUnmutesWithoutTargetConfirmation()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.95, is_volume_muted = true });
        var call = f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        for (var i = 0; i < 5; i++)
        {
            await WaitUntil(() => call.IsCompleted || clock.HasTimer(TimeSpan.FromMilliseconds(500)));
            Assert.False(call.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }
        using var reply = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(ConfirmationTimeout + Effects, Text(reply));
        Assert.Equal(6, f.Calls.Count(c => c.StartsWith("GET", StringComparison.Ordinal)));
        Assert.Equal(new[] { "POST /api/services/media_player/volume_set" }, f.Calls.Where(c => c.StartsWith("POST", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task ImplicitFallbackFinalConfirmationKeepsOriginalDeadline()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var reads = 0;
        f.Respond = (req, _) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                reads++;
                f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = reads == 1 ? 0.95 : 0.4, is_volume_muted = true });
            }
            if (req.RequestUri!.AbsolutePath.EndsWith("/volume_set", StringComparison.Ordinal))
                clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(f.Response(req));
        };
        var call = f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\"}");
        await WaitUntil(() => reads == 2 && clock.HasTimer(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(2, reads);
        Assert.Equal(1, f.Calls.Count(c => c == "POST /api/services/media_player/volume_mute"));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await WaitUntil(() => reads == 3 && clock.HasTimer(TimeSpan.FromMilliseconds(500)));
        Assert.False(call.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        using var reply = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(ConfirmationTimeout + Effects, Text(reply));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(3, reads);
        Assert.Equal(2, f.Posts);
        Assert.DoesNotContain("POST /api/services/tts/speak", f.Calls);
    }

    private static Dictionary<string, JsonElement> ImplicitArgs() => HomeAssistantControlPolicyTests.Arguments(
        "media_player", "speak", "{\"message\":\"Teszt\"}");

    private sealed class PendingSafetyCheck : IKillSwitchStateSource
    {
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken)
        {
            if (!Block) return false;
            Entered.TrySetResult();
            await Release.Task;
            return false;
        }
    }

    [Fact]
    public async Task ImplicitDeadlineIncludesInitialSafetyGuardAndLateGuardCannotReachHa()
    {
        var clock = new Clock();
        var safety = new PendingSafetyCheck();
        await using var f = Fixture(clock, s => s.AddSingleton<IKillSwitchStateSource>(safety));
        var service = f.App.Services.GetRequiredService<HomeAssistantControlService>();
        safety.Block = true;
        var call = service.ExecuteAsync(ImplicitArgs(), RiskLevel.Control, CancellationToken.None);
        await safety.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("3 másodperc", error.Message);
        safety.Release.TrySetResult();
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task ImplicitBodyReadIsCancelledAtOriginalDeadline()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        using var body = new PendingBody();
        f.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        var call = f.App.Services.GetRequiredService<HomeAssistantControlService>().ExecuteAsync(ImplicitArgs(), RiskLevel.Control, CancellationToken.None);
        var token = await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("3 másodperc", error.Message);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task LateImplicitVolumePostCannotStartSpeechOrRepeatAfterDeadline()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.9, is_volume_muted = false });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Respond = async (request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                entered.TrySetResult();
                await release.Task;
                finished.TrySetResult();
            }
            return f.Response(request);
        };
        var call = f.App.Services.GetRequiredService<HomeAssistantControlService>().ExecuteAsync(ImplicitArgs(), RiskLevel.Control, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("3 másodperc", error.Message);
        release.TrySetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "GET /api/states/media_player.room", "POST /api/services/media_player/volume_set" }, f.Calls.ToArray());
    }

    [Fact]
    public async Task ImplicitRecoveryUsesMonotonicReadSpacingEvenAfterEarlyTimer()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var reads = new System.Collections.Concurrent.ConcurrentQueue<long>();
        f.Respond = (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                reads.Enqueue(clock.GetTimestamp());
                f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = reads.Count == 1 ? 0.9 : 0.6, is_volume_muted = false });
            }
            return Task.FromResult(f.Response(request));
        };
        var service = f.App.Services.GetRequiredService<HomeAssistantControlService>();
        var call = service.ExecuteAsync(ImplicitArgs(), RiskLevel.Control, CancellationToken.None);
        await WaitUntil(() => clock.HasTimer(TimeSpan.FromMilliseconds(500)));
        clock.FireEarly(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(499.5));
        await WaitUntil(() => clock.HasTimer(TimeSpan.FromMilliseconds(1)) || call.IsCompleted || reads.Count > 1);
        Assert.Single(reads);
        Assert.False(call.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await call.WaitAsync(TimeSpan.FromSeconds(5));
        var times = reads.ToArray();
        Assert.Equal(2, times.Length);
        Assert.True(TimeSpan.FromTicks(times[1] - times[0]) >= TimeSpan.FromMilliseconds(500));
        Assert.Equal(2, f.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateImplicitReadCannotStartEffectsAfterDeadlineOrCancellation(bool callerCancels)
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Respond = async (request, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.25, is_volume_muted = false });
            finished.TrySetResult();
            return f.Response(request);
        };
        using var caller = new CancellationTokenSource();
        var service = f.App.Services.GetRequiredService<HomeAssistantControlService>();
        var call = service.ExecuteAsync(ImplicitArgs(), RiskLevel.Control, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancels) caller.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        if (!callerCancels) Assert.Contains("3 másodperc", error.Message);
        release.TrySetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, f.Posts);
        Assert.Single(f.Calls);
    }

    [Fact]
    public async Task ImplicitReadAndSpeechKeepMediaLockAgainstConcurrentVolumeWrite()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.25, is_volume_muted = false });
        f.Respond = async (req, _) =>
        {
            if (req.Method == HttpMethod.Get && !entered.Task.IsCompleted)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return f.Response(req);
        };
        var service = f.App.Services.GetRequiredService<HomeAssistantControlService>();
        var speech = service.ExecuteAsync(ImplicitArgs(), RiskLevel.Control, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var write = service.ExecuteAsync(HomeAssistantControlPolicyTests.Arguments("media_player", "volume_set", "{\"volume_level\":0.6}"), RiskLevel.Control, CancellationToken.None);
        Assert.Single(f.Calls);
        Assert.False(write.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(speech, write).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "GET /api/states/media_player.room", "POST /api/services/tts/speak",
            "POST /api/services/media_player/volume_set", "GET /api/states/media_player.room" }, f.Calls.ToArray());
    }

    private static ControlFixture Fixture(Clock clock, Action<IServiceCollection>? configure = null) => new(s =>
    {
        s.AddSingleton(HomeAssistantMediaPlayerTests.Policy());
        s.AddSingleton<TimeProvider>(clock);
        configure?.Invoke(s);
    }, ["media_player"]) { Entity = "media_player.room", Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.9 }) };

    private static Dictionary<string, JsonElement> Args() => HomeAssistantControlPolicyTests.Arguments(
        "media_player", "speak", "{\"message\":\"Teszt\",\"volume_level\":0.4}");

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    [Theory]
    [InlineData(5_000)]
    [InlineData(1)]
    public async Task EarlyTimerWakeupCannotStartNextGetBeforeMonotonicMinimum(long remainingTicks)
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var reads = new System.Collections.Concurrent.ConcurrentQueue<long>();
        f.Respond = (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                reads.Enqueue(clock.GetTimestamp());
                if (reads.Count == 2) f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.4 });
            }
            return Task.FromResult(f.Response(request));
        };
        var service = f.App.Services.GetRequiredService<HomeAssistantControlService>();
        var call = service.ExecuteAsync(Args(), RiskLevel.Control, CancellationToken.None);
        await WaitUntil(() => clock.HasTimer(TimeSpan.FromMilliseconds(500)));
        var remaining = TimeSpan.FromTicks(remainingTicks);
        // Csak az 500 ms-os timer jelez korán; ugyanaz a monoton óra még a határ előtt jár.
        clock.FireEarly(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500) - remaining);
        await WaitUntil(() => reads.Count > 1 || call.IsCompleted || clock.HasTimer(TimeSpan.FromMilliseconds(1)));
        Assert.True(reads.Count == 1, $"Korai második GET: virtuális monoton idő={clock.GetTimestamp()} tick, GET={reads.Count}.");
        Assert.False(call.IsCompleted);
        // A pozitív szubmilliszekundumos maradék nem lehet szinkron Task.Delay(0) ciklus.
        Assert.True(clock.HasTimer(TimeSpan.FromMilliseconds(1)), "A maradékhoz nincs pozitív, aszinkron timer.");
        clock.Advance(remaining);
        Assert.Single(reads);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await call.WaitAsync(TimeSpan.FromSeconds(5));
        var times = reads.ToArray();
        Assert.Equal(2, times.Length);
        Assert.True(TimeSpan.FromTicks(times[1] - times[0]) >= TimeSpan.FromMilliseconds(500));
        Assert.True(TimeSpan.FromTicks(times[1]) < TimeSpan.FromSeconds(3));
        Assert.Equal(2, f.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyWakeupRemainderHonorsOriginalDeadlineAndCallerCancellation(bool callerCancels)
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        using var caller = new CancellationTokenSource();
        var service = f.App.Services.GetRequiredService<HomeAssistantControlService>();
        var call = service.ExecuteAsync(Args(), RiskLevel.Control, caller.Token);
        await WaitUntil(() => clock.HasTimer(TimeSpan.FromMilliseconds(500)));
        clock.FireEarly(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(499.5));
        await WaitUntil(() => clock.HasTimer(TimeSpan.FromMilliseconds(1)) ||
            f.Calls.Count(c => c.StartsWith("GET", StringComparison.Ordinal)) > 1);
        Assert.Equal(1, f.Calls.Count(c => c.StartsWith("GET", StringComparison.Ordinal)));
        if (callerCancels) caller.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(3) - TimeSpan.FromTicks(clock.GetTimestamp()));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal((callerCancels ? "A felolvasási művelet megszakadt." : ConfirmationTimeout) + Effects, error.Message);
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.4 });
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, f.Posts);
        Assert.Equal(1, f.Calls.Count(c => c.StartsWith("GET", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DelayedVolumeIsConfirmedWithSpacedReadsBeforeSpeakingOnce()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var reads = new List<long>();
        f.Respond = (req, _) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                lock (reads) reads.Add(clock.GetTimestamp());
                if (clock.GetTimestamp() >= TimeSpan.TicksPerSecond)
                    f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.4 });
            }
            return Task.FromResult(f.Response(req));
        };
        var call = f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\",\"volume_level\":0.4}");
        for (var i = 0; i < 2; i++)
        {
            await WaitUntil(() => call.IsCompleted || clock.HasTimer(TimeSpan.FromMilliseconds(500)));
            Assert.False(call.IsCompleted, "Az első, régi hangerő nem végleges elutasítás.");
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }
        using var reply = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(ControlFixture.Failed(reply));
        Assert.Equal(new long[] { 0, TimeSpan.TicksPerSecond / 2, TimeSpan.TicksPerSecond }, reads);
        Assert.Equal(2, f.Posts);
        Assert.Equal(1, f.Calls.Count(c => c == "POST /api/services/tts/speak"));
    }

    private const string Effects = " A hangerőállítás vagy a felolvasás hatása már megtörténhetett; nincs visszagörgetés vagy újrapróbálás.";
    private const string ConfirmationTimeout = "A hangerő-visszaigazolás nem érkezett meg 3 másodpercen belül.";
    private const string Uncertain = "A Home Assistant hívás eredménye bizonytalan (transport- vagy válaszhiba).";

    private static string Text(JsonDocument reply) => reply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    [Theory]
    [InlineData("{\"volume_level\":0.9}")]
    [InlineData("{}")]
    [InlineData("{\"volume_level\":0.400000000000000000001}")]
    public async Task UnconfirmedVolumeStopsAtThreeSecondsWithSixReads(string attributes)
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        f.Attributes = JsonDocument.Parse(attributes).RootElement.Clone();
        var call = f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\",\"volume_level\":0.4}");
        for (var i = 0; i < 6; i++)
        {
            await WaitUntil(() => call.IsCompleted ||
                f.Calls.Count(c => c.StartsWith("GET", StringComparison.Ordinal)) == i + 1 && clock.HasTimer(TimeSpan.FromMilliseconds(500)));
            Assert.False(call.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }
        using var reply = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(ConfirmationTimeout + Effects, Text(reply));
        Assert.Equal(6, f.Calls.Count(c => c.StartsWith("GET", StringComparison.Ordinal)));
        Assert.Equal(1, f.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightReadIsBoundedAndCancelledWithoutLateSpeech(bool callerCancels)
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Respond = (req, ct) =>
        {
            if (req.Method == HttpMethod.Post) return Task.FromResult(f.Response(req));
            entered.TrySetResult(ct);
            return late.Task;
        };
        using var caller = new CancellationTokenSource();
        var call = f.App.Services.GetRequiredService<HomeAssistantControlService>().ExecuteAsync(Args(), RiskLevel.Control, caller.Token);
        var readToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancels) caller.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(readToken.IsCancellationRequested);
        Assert.Equal((callerCancels ? "A felolvasási művelet megszakadt." : ConfirmationTimeout) + Effects, error.Message);
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.4 });
        late.SetResult(f.Response(new HttpRequestMessage(HttpMethod.Get, "https://ha.example/api/states/media_player.room")));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, f.Posts);
        Assert.Equal(1, f.Calls.Count(c => c.StartsWith("GET", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("volume", false, "A Home Assistant elutasította a hangerőállítást.")]
    [InlineData("speech", false, "A Home Assistant elutasította a felolvasást.")]
    [InlineData("speech", true, Uncertain)]
    [InlineData("read", true, Uncertain)]
    public async Task ApprovedFailureCategoryReachesActualStatusOutcomePushAndAudit(string stage, bool transport, string category)
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var canary = Guid.NewGuid().ToString("N");
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.4 });
        var executing = false;
        f.Respond = (req, _) =>
        {
            var fail = stage switch
            {
                "volume" => req.RequestUri!.AbsolutePath.EndsWith("/volume_set", StringComparison.Ordinal),
                "speech" => req.RequestUri!.AbsolutePath.EndsWith("/tts/speak", StringComparison.Ordinal),
                _ => req.Method == HttpMethod.Get
            };
            if (!executing || !fail) return Task.FromResult(f.Response(req));
            if (transport) throw new HttpRequestException(canary, new Exception(canary));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(canary) });
        };
        using var pending = await f.Call("ha_sensitive_control", "media_player", "speak", "{\"message\":\"Teszt\",\"volume_level\":0.4}");
        Assert.False(ControlFixture.Failed(pending));
        var notice = Assert.Single(f.Channel.Requests);
        executing = true;
        var outcome = await f.Channel.Approve();
        var expected = category + Effects;
        Assert.Equal(ApprovalState.Failed, outcome.State);
        Assert.True(expected == outcome.Result, "A kategória elveszett az értesítésből.");
        var status = f.App.Services.GetRequiredService<ApprovalGate>().GetStatus(notice.Id)!;
        Assert.True(expected == status.Result, "A kategória elveszett a státuszból.");
        using var statusReply = await HomeAssistantToolProtocolTests.SendAsync(f.Client, "tools/call", new { name = "gate_approval_status", arguments = new { requestId = notice.Id.ToString() } });
        Assert.True(Text(statusReply) == "sikertelen: " + expected, "Az MCP státusz kategóriája eltér.");
        var push = await HomeAssistantApprovalChannelTests.CaptureTtsOutcomePush(notice, outcome, clock);
        Assert.True(push.Contains(category, StringComparison.Ordinal), "A push kategóriája elveszett.");
        Assert.False(push.Contains(canary, StringComparison.Ordinal), "Nyers providerhiba a pushban.");
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.App.AuditPath};Pooling=False");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT SanitizedError FROM AuditCalls WHERE State = 'Failed'";
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            Assert.True(reader.GetString(0) == expected, "Az eredeti vagy végrehajtási audit kategóriája eltér.");
            count++;
        }
        Assert.Equal(2, count);
        Assert.Equal(stage == "speech" ? 2 : 1, f.Posts);
    }

    [Fact]
    public async Task ApprovedConfirmationTimeoutRemainsSpecificInStatusOutcomeAndAudit()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        using var pending = await f.Call("ha_sensitive_control", "media_player", "speak", "{\"message\":\"Teszt\",\"volume_level\":0.6}");
        var outcomeTask = f.Channel.Approve();
        await WaitUntil(() => clock.HasTimer(TimeSpan.FromMilliseconds(500)));
        clock.Advance(TimeSpan.FromSeconds(3));
        var outcome = await outcomeTask;
        var expected = ConfirmationTimeout + Effects;
        Assert.Equal(ApprovalState.Failed, outcome.State);
        Assert.Equal(expected, outcome.Result);
        var notice = Assert.Single(f.Channel.Requests);
        using var status = await HomeAssistantToolProtocolTests.SendAsync(f.Client, "tools/call", new { name = "gate_approval_status", arguments = new { requestId = notice.Id.ToString() } });
        Assert.Equal("sikertelen: " + expected, Text(status));
        var push = await HomeAssistantApprovalChannelTests.CaptureTtsOutcomePush(notice, outcome, clock);
        Assert.Contains(ConfirmationTimeout, push);
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.App.AuditPath};Pooling=False");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM AuditCalls WHERE State = 'Failed' AND SanitizedError = $error";
        command.Parameters.AddWithValue("$error", expected);
        Assert.Equal(2L, await command.ExecuteScalarAsync());
        Assert.Equal(1, f.Posts);
    }

    [Theory]
    [InlineData("kill")]
    [InlineData("quiet")]
    [InlineData("cancel")]
    public async Task WaitingRechecksSafetyAndCancellationBeforeAnotherReadOrSpeech(string reason)
    {
        var clock = new Clock { Origin = DateTimeOffset.Parse("2026-11-17T20:59:59.500Z") };
        await using var f = Fixture(clock);
        using var caller = new CancellationTokenSource();
        var call = f.App.Services.GetRequiredService<HomeAssistantControlService>().ExecuteAsync(Args(), RiskLevel.Control, caller.Token);
        await WaitUntil(() => clock.HasTimer(TimeSpan.FromMilliseconds(500)));
        if (reason == "kill") await File.WriteAllTextAsync(f.App.KillSwitchPath, "{\"Enabled\":true}");
        if (reason == "cancel") caller.Cancel();
        else
        {
            if (reason == "kill") clock.Origin = clock.Origin.AddHours(-1);
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }
        var error = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(reason switch
        {
            "kill" => "biztonsági ellenőrzés letiltotta",
            "quiet" => "ha_sensitive_control",
            _ => "megszakadt"
        }, error.Message);
        Assert.Equal(1, f.Posts);
        Assert.Equal(1, f.Calls.Count(c => c.StartsWith("GET", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SlowReadConsumesTheSameDeadlineAndDoesNotBuyAnotherPollingWindow()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Respond = async (req, ct) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                entered.TrySetResult();
                await finishRead.Task.WaitAsync(ct);
            }
            return f.Response(req);
        };
        var call = f.App.Services.GetRequiredService<HomeAssistantControlService>().ExecuteAsync(Args(), RiskLevel.Control, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMilliseconds(2750));
        finishRead.SetResult();
        await WaitUntil(() => clock.HasTimer(TimeSpan.FromMilliseconds(500)));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ConfirmationTimeout + Effects, error.Message);
        Assert.Single(f.Calls, c => c.StartsWith("GET", StringComparison.Ordinal));
        Assert.Equal(1, f.Posts);
    }

    [Theory]
    [InlineData("forged-category")]
    [InlineData("gate-message")]
    [InlineData("http-500")]
    [InlineData("http-408")]
    [InlineData("invalid-json")]
    public async Task ProviderCannotForgeTrustedCategoryOrLeakRawData(string kind)
    {
        var clock = new Clock();
        var logs = new Audit.McpAuditTests.RecordingLogger();
        await using var f = Fixture(clock, s => s.AddLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)));
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.4 });
        var canary = Guid.NewGuid().ToString("N");
        f.Respond = (req, _) =>
        {
            if (!req.RequestUri!.AbsolutePath.EndsWith("/tts/speak", StringComparison.Ordinal)) return Task.FromResult(f.Response(req));
            if (kind == "forged-category") throw ForgedCategory();
            if (kind == "gate-message") throw new AgentGate.Tools.GateToolException(canary);
            var code = kind switch { "http-500" => HttpStatusCode.InternalServerError, "http-408" => HttpStatusCode.RequestTimeout, _ => HttpStatusCode.OK };
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(canary) });
        };
        using var reply = await f.Call("ha_control", "media_player", "speak", "{\"message\":\"Teszt\",\"volume_level\":0.4}");
        Assert.True(ControlFixture.Failed(reply));
        Assert.True(Text(reply) == Uncertain + Effects, "A provider hamis kategóriája vagy szövege átjutott.");
        Assert.False(logs.Contains(canary), "Nyers providerhiba a naplóban.");
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.App.AuditPath};Pooling=False");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT SanitizedError FROM AuditCalls WHERE State = 'Failed'";
        Assert.True((string?)await command.ExecuteScalarAsync() == Uncertain + Effects, "A provider hamis kategóriája vagy szövege az auditba került.");
        Assert.Equal(2, f.Posts);
    }

    private static Exception ForgedCategory()
    {
        // A hibás infrastruktúra még a belső típust sem csempészheti át a kliens normalizálásán.
        var assembly = typeof(ControlPolicy).Assembly;
        var category = Enum.Parse(assembly.GetType("AgentGate.Integrations.HomeAssistant.Control.TtsFailure")!, "VolumeConfirmationTimeout");
        return (Exception)Activator.CreateInstance(assembly.GetType("AgentGate.Integrations.HomeAssistant.Control.TtsExecutionException")!, category)!;
    }

    [Fact]
    public async Task ResponseBodyReadIsAlsoCancelledAtTheSharedDeadline()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        using var body = new PendingBody();
        f.Respond = (req, _) => Task.FromResult(req.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }
            : f.Response(req));
        var call = f.App.Services.GetRequiredService<HomeAssistantControlService>().ExecuteAsync(Args(), RiskLevel.Control, CancellationToken.None);
        var readToken = await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ConfirmationTimeout + Effects, error.Message);
        Assert.True(readToken.IsCancellationRequested);
        Assert.Equal(1, f.Posts);
    }

    private sealed class PendingBody : MemoryStream
    {
        public TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task MediaLockCoversPollingAndIsReleasedAfterTimeout()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var service = f.App.Services.GetRequiredService<HomeAssistantControlService>();
        var speech = service.ExecuteAsync(Args(), RiskLevel.Control, CancellationToken.None);
        await WaitUntil(() => clock.HasTimer(TimeSpan.FromMilliseconds(500)));
        var other = service.ExecuteAsync(HomeAssistantControlPolicyTests.Arguments("media_player", "turn_on", entity: "media_player.room"), RiskLevel.Control, CancellationToken.None);
        Assert.False(other.IsCompleted);
        Assert.Equal(1, f.Posts);
        clock.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<Exception>(() => speech.WaitAsync(TimeSpan.FromSeconds(5)));
        await other.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, f.Posts);
        Assert.DoesNotContain("POST /api/services/tts/speak", f.Calls);
    }

    [Fact]
    public async Task TimeoutAttemptsConsumeTheSharedQuotaUntilCompletionPlusSixtySeconds()
    {
        var clock = new Clock();
        await using var f = Fixture(clock);
        var service = f.App.Services.GetRequiredService<HomeAssistantControlService>();
        for (var i = 0; i < 3; i++)
        {
            var call = service.ExecuteAsync(Args(), i == 1 ? RiskLevel.SensitiveControl : RiskLevel.Control, CancellationToken.None);
            await WaitUntil(() => clock.HasTimer(TimeSpan.FromMilliseconds(500)));
            clock.Advance(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(3, f.Posts);
        clock.Advance(TimeSpan.FromSeconds(51));
        await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteAsync(Args(), RiskLevel.Control, CancellationToken.None));
        Assert.Equal(3, f.Posts);
        clock.Advance(TimeSpan.FromSeconds(3));
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.4 });
        await service.ExecuteAsync(Args(), RiskLevel.Control, CancellationToken.None);
        Assert.Equal(5, f.Posts);
    }

    [Fact]
    public async Task AuditCompletionFailureCannotMasqueradeAsTtsFailure()
    {
        var clock = new Clock();
        var store = new CompletionFailureStore();
        await using var f = Fixture(clock, s => s.AddSingleton<IAuditStore>(store));
        f.Attributes = JsonSerializer.SerializeToElement(new { volume_level = 0.4 });
        using var pending = await f.Call("ha_sensitive_control", "media_player", "speak", "{\"message\":\"Teszt\",\"volume_level\":0.4}");
        var outcome = await f.Channel.Approve();
        Assert.Equal(ApprovalState.Failed, outcome.State);
        Assert.Equal("A jóváhagyott művelet vagy az audit lezárása sikertelen; ez nem visszagörgetés.", outcome.Result);
        Assert.Equal(2, f.Posts);
    }

    private sealed class CompletionFailureStore : IAuditStore
    {
        private Guid executionId;
        public Task BeginAsync(AuditCall call, CancellationToken cancellationToken)
        {
            if (call.Tool == "ha_sensitive_control" && call.SanitizedParameters.Contains("\"phase\":\"execution\"", StringComparison.Ordinal))
                executionId = call.CallId;
            return Task.CompletedTask;
        }
        public Task CompleteAsync(Guid callId, string state, double durationMilliseconds, string? sanitizedError, CancellationToken cancellationToken)
        {
            if (callId == executionId && state == "Succeeded") throw ForgedCategory();
            return Task.CompletedTask;
        }
        public Task CompletePendingAsync(Guid callId, string state, string? sanitizedError, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // A meglévő privát HeartbeatClock mintája: monoton kézi óra, valódi TimeProvider-időzítőkkel.
    private sealed class Clock : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ManualTimer> timers = [];
        private long ticks;
        public DateTimeOffset Origin { get; set; } = DateTimeOffset.Parse("2026-11-17T12:00:00Z");
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (sync) return ticks; }
        public override DateTimeOffset GetUtcNow() => Origin.AddTicks(GetTimestamp());
        public bool HasTimer(TimeSpan delay)
        {
            lock (sync) return timers.Any(t => t.Due is { } due && due - ticks == delay.Ticks);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (sync) timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            List<ManualTimer> ready;
            lock (sync)
            {
                ticks += amount.Ticks;
                ready = timers.Where(t => t.Due is { } due && due <= ticks).ToList();
                foreach (var timer in ready) timer.Due = timer.Period > TimeSpan.Zero ? ticks + timer.Period.Ticks : null;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        public void FireEarly(TimeSpan requestedDelay, TimeSpan elapsed)
        {
            ManualTimer timer;
            lock (sync)
            {
                timer = timers.Single(t => t.Due is { } due && due - ticks == requestedDelay.Ticks);
                Assert.True(elapsed > TimeSpan.Zero && elapsed < requestedDelay);
                ticks += elapsed.Ticks;
                timer.Due = null;
            }
            timer.Callback(timer.State);
        }
        private sealed class ManualTimer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long? Due { get; set; }
            public TimeSpan Period { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.sync)
                {
                    if (disposed) return false;
                    Period = period;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.ticks + dueTime.Ticks;
                    return true;
                }
            }
            public void Dispose() { lock (owner.sync) { disposed = true; Due = null; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

public sealed partial class HomeAssistantApprovalChannelTests
{
    internal static async Task<string> CaptureTtsOutcomePush(ApprovalNotice notice, ApprovalOutcome outcome, TimeProvider clock)
    {
        await using var harness = new Standalone(clock: clock);
        await harness.StartAsync();
        await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
        await harness.Channel.SendOutcomeAsync(outcome, CancellationToken.None);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        return body.RootElement.GetProperty("message").GetString()!;
    }
}
