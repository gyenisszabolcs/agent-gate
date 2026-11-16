using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentGate.Approval;
using AgentGate.Audit;
using AgentGate.Approval.HomeAssistant;
using AgentGate.Policy;
using AgentGate.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Theory]
    [InlineData(RiskLevel.SensitiveControl, "érzékeny vezérlés")]
    [InlineData(RiskLevel.Configuration, "konfiguráció")]
    [InlineData(RiskLevel.System, "rendszer")]
    public async Task RequestPresentationUsesHungarianRiskBudapestTimeAndParameterLines(RiskLevel risk, string label)
    {
        var clock = new MutableClock { Now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero) };
        await using var harness = new Standalone(clock: clock);
        await harness.StartAsync();
        var notice = Notice() with { Risk = risk, Summary = "{\"text\":\"próba\",\"count\":\"2\"}", ExpiresAt = clock.Now.AddMinutes(15) };
        await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("Kockázat: " + label + "\n", message);
        Assert.Contains("Lejárat (Europe/Budapest): 13:15", message);
        Assert.Contains("text: próba\ncount: 2\n", message);
        Assert.DoesNotContain("{", message);
        Assert.Equal(clock.Now.AddMinutes(15), notice.ExpiresAt);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(300)]
    public async Task QuietLiveConnectionUsesIncreasingPingsWithoutReconnectOrRepeatedSecretLookup(int maxIdleSeconds)
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(clock: clock, configure: options => options.MaxIdleSeconds = maxIdleSeconds);
        await harness.StartAsync();
        var interval = TimeSpan.FromMilliseconds(Math.Min(20_000, maxIdleSeconds * 1000 / 3));
        // Több teljes MaxIdle ablakon át kizárólag pong érkezik, jóváhagyási esemény nem.
        var cycles = maxIdleSeconds / interval.TotalSeconds * 2 + 1;
        for (var index = 0; index < cycles; index++)
        {
            await WaitAsync(() => clock.HasTimerDueIn(interval));
            clock.Advance(interval);
            await WaitAsync(() => harness.Socket.PingIds.Count == index + 1);
            await WaitAsync(() => clock.HasTimerDueIn(interval));
            Assert.True(harness.Channel.IsAvailable);
            Assert.Equal(1, harness.Factory.Count);
            Assert.Equal(1, harness.Secrets.Calls);
        }
        var ids = harness.Socket.PingIds.ToArray();
        Assert.Equal(1, harness.Socket.MaxActiveSends);
        Assert.Equal(0, harness.Socket.ActiveSends);
        Assert.True(ids[0] > 1 && ids.Zip(ids.Skip(1), (left, right) => right > left).All(increasing => increasing),
            "A heartbeat-azonosítók nem növekednek a feliratkozás azonosítója után.");
        await harness.Channel.SendRequestAsync(Notice(), CancellationToken.None);
        Assert.Equal(1, harness.Http.Count);
        Assert.Equal(1, harness.Factory.Count);
        Assert.Equal(2, harness.Secrets.Calls); // Az új REST-küldés külön tokenlekérése, nem reconnect.
        await harness.Channel.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-id")]
    [InlineData("string-id")]
    [InlineData("missing-id")]
    [InlineData("events")]
    public async Task MissingMatchingPongReconnectsAtDeadlineEvenWithUnrelatedTraffic(string kind)
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(clock: clock);
        harness.Socket.AutoPong = false;
        harness.Factory.ReconnectAutoHandshake = false;
        await harness.StartAsync();
        await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(20)));
        clock.Advance(TimeSpan.FromSeconds(20));
        await WaitAsync(() => harness.Socket.PingIds.Count == 1);
        var ping = harness.Socket.PingIds.Single();
        for (var index = 0; index < 3; index++)
        {
            if (kind != "missing")
            {
                var reads = harness.Socket.ReceiveCalls;
                var payload = kind switch
                {
                    "wrong-id" => JsonSerializer.Serialize(new { id = ping + 1, type = "pong" }),
                    "string-id" => JsonSerializer.Serialize(new { id = ping.ToString(System.Globalization.CultureInfo.InvariantCulture), type = "pong" }),
                    "missing-id" => "{\"type\":\"pong\"}",
                    _ => Event(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)))
                };
                harness.Socket.Incoming.Writer.TryWrite(payload);
                await WaitAsync(() => harness.Socket.ReceiveCalls > reads);
            }
            await WaitAsync(() => clock.ActiveTimers > 0);
            clock.Advance(TimeSpan.FromSeconds(10));
            Assert.True(harness.Channel.IsAvailable);
            Assert.Equal(1, harness.Factory.Count);
            Assert.Equal(1, harness.Secrets.Calls);
        }
        await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(10));
        await WaitAsync(() => !harness.Channel.IsAvailable && clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, harness.Socket.ActiveReceives);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => harness.Factory.Count == 2 && harness.Secrets.Calls == 2);
        Assert.False(harness.Channel.IsAvailable); // Új kapcsolat auth/ACK előtt nem használható.
    }

    [Fact]
    public async Task PreviousPongCannotSatisfyNextPingAndHeartbeatDoesNotEatApprovalEvents()
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(clock: clock);
        await harness.StartAsync();
        await harness.Channel.SendRequestAsync(Notice(), CancellationToken.None);
        var (approve, _) = Codes(harness.Http);
        await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(20)));
        clock.Advance(TimeSpan.FromSeconds(20));
        await WaitAsync(() => harness.Socket.PingIds.Count == 1 && clock.HasTimerDueIn(TimeSpan.FromSeconds(20)));
        var previous = harness.Socket.PingIds.Single();
        harness.Socket.AutoPong = false;
        clock.Advance(TimeSpan.FromSeconds(20));
        await WaitAsync(() => harness.Socket.PingIds.Count == 2);
        var reads = harness.Socket.ReceiveCalls;
        harness.Socket.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new { id = previous, type = "pong" }));
        harness.Socket.Incoming.Writer.TryWrite(Event(approve));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var decisions = harness.Channel.ReadDecisionsAsync(deadline.Token).GetAsyncEnumerator();
        Assert.True(await decisions.MoveNextAsync());
        Assert.Equal(ApprovalChoice.Approve, decisions.Current.Choice);
        await WaitAsync(() => harness.Socket.ReceiveCalls >= reads + 2 && clock.HasTimerDueIn(TimeSpan.FromSeconds(40)));
        clock.Advance(TimeSpan.FromSeconds(40));
        await WaitAsync(() => !harness.Channel.IsAvailable);
        Assert.Equal(1, harness.Socket.MaxActiveReceives);
    }

    [Fact]
    public async Task UnsolicitedPongCannotPostponeFirstPing()
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(clock: clock);
        await harness.StartAsync();
        await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(20)));
        clock.Advance(TimeSpan.FromSeconds(10));
        var reads = harness.Socket.ReceiveCalls;
        harness.Socket.Incoming.Writer.TryWrite("{\"id\":99,\"type\":\"pong\"}");
        await WaitAsync(() => harness.Socket.ReceiveCalls > reads && clock.HasTimerDueIn(TimeSpan.FromSeconds(10)));
        clock.Advance(TimeSpan.FromSeconds(10));
        await WaitAsync(() => harness.Socket.PingIds.Count == 1 && clock.HasTimerDueIn(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, harness.Factory.Count);
        Assert.Equal(1, harness.Secrets.Calls);
        Assert.Equal(1, harness.Socket.MaxActiveSends);
    }

    [Fact]
    public async Task PingSendDeadlineCannotOutliveSmallerRemainingLivenessWindow()
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(clock: clock, configure: options => options.MaxIdleSeconds = 5);
        harness.Socket.BlockPingSend = true;
        await harness.StartAsync();
        var interval = TimeSpan.FromMilliseconds(5 * 1000 / 3);
        var remaining = TimeSpan.FromSeconds(5) - interval;
        await WaitAsync(() => clock.HasTimerDueIn(interval));
        clock.Advance(interval);
        await WaitAsync(() => harness.Socket.PingIds.Count == 1 && clock.HasTimerDueIn(remaining));
        Assert.Equal(1, harness.Socket.ActiveSends);
        clock.Advance(remaining);
        await WaitAsync(() => !harness.Channel.IsAvailable && clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, harness.Socket.ActiveSends);
        Assert.Equal(0, harness.Socket.ActiveReceives);
        Assert.Equal(1, harness.Secrets.Calls);
    }

    [Fact]
    public async Task DelayedHeartbeatCannotTurnExpiredWindowIntoInfinitePingSend()
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(clock: clock);
        harness.Socket.BlockPingSend = true;
        harness.Factory.ReconnectAutoHandshake = false;
        await harness.StartAsync();
        await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(20)));
        // A késve ébredő hurok régen -1 ms-ot, azaz végtelen küldési határidőt számolt.
        clock.Advance(TimeSpan.FromMilliseconds(60_001));
        await WaitAsync(() => !harness.Channel.IsAvailable && clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));
        Assert.Empty(harness.Socket.PingIds);
        Assert.Equal(0, harness.Socket.ActiveReceives);
        Assert.Equal(1, harness.Secrets.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => harness.Factory.Count == 2 && harness.Secrets.Calls == 2);
    }

    [Fact]
    public async Task BlockedPingSendIsBoundedAndDisconnectsWithoutLeavingAnActiveReceive()
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(clock: clock);
        harness.Socket.BlockPingSend = true;
        await harness.StartAsync();
        await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(20)));
        clock.Advance(TimeSpan.FromSeconds(20));
        await WaitAsync(() => harness.Socket.PingIds.Count == 1 && clock.HasTimerDueIn(TimeSpan.FromSeconds(5)));
        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitAsync(() => !harness.Channel.IsAvailable && clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, harness.Socket.ActiveReceives);
        Assert.Equal(1, harness.Secrets.Calls);
        Assert.Equal(1, harness.Factory.Count);
    }

    [Fact]
    public async Task HeartbeatKeepsOneFragmentedReceiveAndPreservesInterleavedApprovalAndPong()
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(clock: clock);
        await harness.StartAsync();
        await harness.Channel.SendRequestAsync(Notice(), CancellationToken.None);
        var (approve, _) = Codes(harness.Http);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Socket.FragmentRelease = release.Task;
        var payload = JsonNode.Parse(Event(approve))!;
        payload["padding"] = new string('x', 5000);
        harness.Socket.Incoming.Writer.TryWrite(payload.ToJsonString());
        try
        {
            await harness.Socket.FragmentEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(20)));
            clock.Advance(TimeSpan.FromSeconds(20));
            await WaitAsync(() => harness.Socket.PingIds.Count == 1);
            Assert.Equal(1, harness.Socket.ActiveReceives);
            release.TrySetResult();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await using var decisions = harness.Channel.ReadDecisionsAsync(deadline.Token).GetAsyncEnumerator();
            Assert.True(await decisions.MoveNextAsync());
            Assert.Equal(ApprovalChoice.Approve, decisions.Current.Choice);
            await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(20)));
            Assert.True(harness.Channel.IsAvailable);
            Assert.Equal(1, harness.Factory.Count);
            Assert.Equal(1, harness.Socket.MaxActiveReceives);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task PongAtExactDeadlineCannotReviveDeadConnection()
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(clock: clock);
        harness.Socket.AutoPong = false;
        await harness.StartAsync();
        await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(20)));
        clock.Advance(TimeSpan.FromSeconds(20));
        await WaitAsync(() => harness.Socket.PingIds.Count == 1 && clock.HasTimerDueIn(TimeSpan.FromSeconds(40)));
        var id = harness.Socket.PingIds.Single();
        clock.Advance(TimeSpan.FromSeconds(40));
        harness.Socket.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new { id, type = "pong" }));
        await WaitAsync(() => !harness.Channel.IsAvailable);
        Assert.Equal(1, harness.Factory.Count);
        Assert.Equal(1, harness.Secrets.Calls);
    }

    [Fact]
    public void ProductionTransportFactoriesDisableRedirectProxyAndCookiesWithoutConnecting()
    {
        using var http = Assert.IsType<SocketsHttpHandler>(new ApprovalHttpHandlerFactory().CreateHandler());
        Assert.False(http.AllowAutoRedirect);
        Assert.False(http.UseProxy);
        Assert.False(http.UseCookies);
        using var factory = new ApprovalWebSocketFactory();
        var invoker = (HttpMessageInvoker)typeof(ApprovalWebSocketFactory).GetField("invoker", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(factory)!;
        var handler = Assert.IsType<SocketsHttpHandler>(typeof(HttpMessageInvoker).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(HttpMessageHandler)).GetValue(invoker));
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task RealProgramOuterTimeoutRevokesEvenLateSuccessfulPushWithoutOrphanOrExecution()
    {
        await using var app = new ChannelApp { GateSendTimeoutSeconds = 1 };
        using var client = app.CreateClient();
        var channel = Assert.Single(app.Services.GetServices<IApprovalChannel>());
        await WaitAsync(() => channel.IsAvailable);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Http.DisposeRelease = release.Task;
        var call = CallMcpAsync(app, "gate_approval_test", new { text = "próba" });
        try
        {
            await app.Http.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var grants = (System.Collections.IDictionary)channel.GetType().GetField("notifications", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(channel)!;
            var id = grants.Keys.Cast<Guid>().Single();
            using var failed = await call;
            Assert.True(failed.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Null(app.Services.GetRequiredService<ApprovalGate>().GetStatus(id));
            app.Clock.Now = app.Clock.Now.AddMinutes(16);
            app.Http.DisposeRelease = null;
            release.TrySetResult();
            await WaitAsync(() => NotificationCount(channel) == 0);
            Assert.Equal(0, app.Operation.Count);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealProgramBusyRequestCannotDropRejectionOrOnlineExpiryOutcome(bool expire)
    {
        await using var app = new ChannelApp();
        using var client = app.CreateClient();
        var channel = Assert.Single(app.Services.GetServices<IApprovalChannel>());
        await WaitAsync(() => channel.IsAvailable);
        using var first = await CallMcpAsync(app, "gate_approval_test", new { text = "első" });
        var text = first.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(text[(text.LastIndexOf(' ') + 1)..], out var id));
        var (_, reject) = Codes(app.Http);
        var expiresAt = app.Clock.Now.AddMinutes(15);
        app.Clock.Now = app.Clock.Now.AddMinutes(1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Http.Release = release.Task;
        var second = CallMcpAsync(app, "gate_approval_test", new { text = "második" });
        try
        {
            await app.Http.BlockingEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (expire) app.Clock.Now = expiresAt;
            else app.Socket.Incoming.Writer.TryWrite(Event(reject));
            var gate = app.Services.GetRequiredService<ApprovalGate>();
            await WaitAsync(() => gate.GetStatus(id)?.State == (expire ? ApprovalState.Expired : ApprovalState.Rejected));
            Assert.Equal(2, app.Http.Count);
            release.TrySetResult();
            using var pending = await second;
            await WaitAsync(() => app.Http.Count == 3);
            Assert.True(app.Http.Body!.Contains(expire ? "lej" : "elutas", StringComparison.Ordinal));
            Assert.Equal(1, NotificationCount(channel));
            Assert.Equal(0, app.Operation.Count);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task RealProgramFailedPendingAuditClosesNotificationAndNeverGrantsExecution()
    {
        await using var app = new ChannelApp();
        var store = new FailPendingStore(app.Secrets.Token);
        app.AuditOverride = store;
        using var client = app.CreateClient();
        var channel = Assert.Single(app.Services.GetServices<IApprovalChannel>());
        await WaitAsync(() => channel.IsAvailable);
        using var failed = await CallMcpAsync(app, "gate_approval_test", new { text = "próba" });
        Assert.True(failed.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        await WaitAsync(() => app.Http.Count == 2);
        Assert.Equal(0, NotificationCount(channel));
        Assert.Equal(0, app.Operation.Count);
        Assert.Equal(ApprovalState.Failed, app.Services.GetRequiredService<ApprovalGate>().GetStatus(store.RequestId)!.State);
        Assert.True(!app.Logs.Contains(app.Secrets.Token) && !failed.RootElement.GetRawText().Contains(app.Secrets.Token, StringComparison.Ordinal));
    }

    private static int NotificationCount(IApprovalChannel channel)
    {
        var value = channel.GetType().GetField("notifications", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(channel)!;
        return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownTokenIsRedactedBeforeRequestAndOutcomeTruncation(bool outcome)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var sensitive = new string('x', 500) + harness.Secrets.Token + "utótag";
        var notice = Notice() with { Summary = outcome ? "próba" : sensitive };
        await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
        if (outcome) await harness.Channel.SendOutcomeAsync(new ApprovalOutcome(notice.Id, ApprovalState.Executed, sensitive), CancellationToken.None);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        Assert.True(!body.RootElement.GetProperty("message").GetString()!.Contains(harness.Secrets.Token[..12], StringComparison.Ordinal), "Ismert tokenrészlet került a hosszkorlátos szövegbe.");
    }

    [Fact]
    public async Task InterveningNewRequestCannotEraseOnlineExpiryOutcome()
    {
        var clock = new MutableClock();
        await using var harness = new Standalone(clock: clock);
        await harness.StartAsync();
        var notice = Notice() with { ExpiresAt = clock.Now.AddTicks(1) };
        await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
        clock.Now = notice.ExpiresAt;
        await harness.Channel.SendRequestAsync(Notice() with { ExpiresAt = clock.Now.AddMinutes(15) }, CancellationToken.None);
        var error = await Record.ExceptionAsync(() => harness.Channel.SendOutcomeAsync(new ApprovalOutcome(notice.Id, ApprovalState.Expired, null), CancellationToken.None));
        Assert.True(error is null, "Az új kérés eltüntette a lejáratértesítést.");
        Assert.Equal(3, harness.Http.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonCooperativeSecretLookupTimesOutWithoutAccumulationAndIsOwnedUntilCompletion(bool synchronous)
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(configure: options => options.TimeoutSeconds = 1, clock: clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Secrets.BlockedTask = release.Task;
        harness.Secrets.BlockSynchronously = synchronous;
        try
        {
            await harness.Channel.StartAsync(CancellationToken.None);
            await WaitAsync(() => harness.Secrets.Calls == 1 && clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));
            clock.Advance(TimeSpan.FromSeconds(1));
            await harness.Socket.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));
            Assert.Equal(1, harness.Factory.Count);
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitAsync(() => harness.Factory.Count >= 2);
            Assert.False(harness.Channel.IsAvailable);
            Assert.Equal(1, harness.Secrets.Calls);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await harness.Channel.StopAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0, harness.Http.Count);
        }
        finally
        {
            release.TrySetResult();
            await harness.Secrets.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task ConcurrentOppositeButtonsProduceOnlyOneImmutableDecision()
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var reader = harness.Channel.ReadDecisionsAsync(timeout.Token).GetAsyncEnumerator();
        var notice = Notice();
        await harness.Channel.SendRequestAsync(notice, timeout.Token);
        var (approve, reject) = Codes(harness.Http);
        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() => harness.Socket.Incoming.Writer.TryWrite(Event(index % 2 == 0 ? approve : reject)))));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(notice.Id, reader.Current.Id);
        Assert.True(reader.Current.Choice is ApprovalChoice.Approve or ApprovalChoice.Reject);
        var fence = Notice();
        await harness.Channel.SendRequestAsync(fence, timeout.Token);
        var (fenceCode, _) = Codes(harness.Http);
        harness.Socket.Incoming.Writer.TryWrite(Event(fenceCode));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(fence.Id, reader.Current.Id);
    }

    [Fact]
    public async Task ExactExpiryBoundaryRejectsBothSendAndLateActionWithoutExtendingLifetime()
    {
        var clock = new MutableClock();
        await using var harness = new Standalone(clock: clock);
        await harness.StartAsync();
        var invalid = Notice() with { ExpiresAt = clock.Now };
        var error = await Record.ExceptionAsync(() => harness.Channel.SendRequestAsync(invalid, CancellationToken.None));
        Assert.True(error is not null);
        Assert.Equal(0, harness.Http.Count);
        var notice = Notice() with { ExpiresAt = clock.Now.AddTicks(1) };
        await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
        var (code, _) = Codes(harness.Http);
        clock.Now = notice.ExpiresAt;
        harness.Socket.Incoming.Writer.TryWrite(Event(code));
        var fence = Notice() with { ExpiresAt = clock.Now.AddMinutes(15) };
        await harness.Channel.SendRequestAsync(fence, CancellationToken.None);
        var (fenceCode, _) = Codes(harness.Http);
        harness.Socket.Incoming.Writer.TryWrite(Event(fenceCode));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var reader = harness.Channel.ReadDecisionsAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(fence.Id, reader.Current.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealProgramExecutionAndOutcomeFailuresNeverLeakOrRetry(bool executionFails)
    {
        await using var app = new ChannelApp();
        using var client = app.CreateClient();
        var channel = Assert.Single(app.Services.GetServices<IApprovalChannel>());
        await WaitAsync(() => channel.IsAvailable);
        using var pending = await CallMcpAsync(app, "gate_approval_test", new { text = "próba" });
        var text = pending.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(text[(text.LastIndexOf(' ') + 1)..], out var id));
        var (approve, reject) = Codes(app.Http);
        if (executionFails) app.Operation.Fail = true;
        else { app.Http.Status = HttpStatusCode.InternalServerError; app.Http.ResponseBody = app.Secrets.Token; }
        app.Socket.Incoming.Writer.TryWrite(Event(approve));
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        await WaitAsync(() => gate.GetStatus(id)?.State == (executionFails ? ApprovalState.Failed : ApprovalState.Executed) && app.Http.Count == 2);
        app.Socket.Incoming.Writer.TryWrite(Event(approve));
        app.Socket.Incoming.Writer.TryWrite(Event(reject));
        app.Http.Status = HttpStatusCode.OK;
        app.Http.ResponseBody = "[]";
        using var next = await CallMcpAsync(app, "gate_approval_test", new { text = "sorompó" });
        var nextText = next.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(nextText[(nextText.LastIndexOf(' ') + 1)..], out var nextId));
        var (_, nextReject) = Codes(app.Http);
        app.Socket.Incoming.Writer.TryWrite(Event(nextReject));
        await WaitAsync(() => gate.GetStatus(nextId)?.State == ApprovalState.Rejected && app.Http.Count == 4);
        Assert.Equal(1, app.Operation.Count);
        Assert.True(!app.Logs.Contains(app.Secrets.Token) && !app.Logs.Contains(approve) && !app.Logs.Contains(reject), "Nyers eredményhiba a logban.");
        using var status = await CallMcpAsync(app, "gate_approval_status", new { requestId = id.ToString() });
        Assert.True(!status.RootElement.GetRawText().Contains(app.Secrets.Token, StringComparison.Ordinal), "Nyers hiba a státuszban.");
    }

    [Theory]
    [InlineData("https://ha.invalid", false, "wss://ha.invalid/api/websocket")]
    [InlineData("http://172.16.0.1:8123", true, "ws://172.16.0.1:8123/api/websocket")]
    public async Task RealProgramUsesSameValidatedOriginAndWarnsSafelyForExplicitHttp(string baseUrl, bool allowHttp, string socketUrl)
    {
        await using var app = new ChannelApp(baseUrl, allowHttp);
        using var client = app.CreateClient();
        var channel = Assert.Single(app.Services.GetServices<IApprovalChannel>());
        await WaitAsync(() => channel.IsAvailable);
        Assert.Equal(new Uri(socketUrl), app.Factory.LastUri);
        await channel.SendRequestAsync(Notice(), CancellationToken.None);
        Assert.Equal(new Uri(new Uri(baseUrl), "/api/services/notify/mobile_app_test"), app.Http.LastUri);
        Assert.Equal(allowHttp, app.Logs.Contains("HTTP-kivétel engedélyezett"));
        Assert.True(!app.Logs.Contains(baseUrl) && !app.Logs.Contains(app.Secrets.Token), "A startupfigyelmeztetés érzékeny részletet tartalmaz.");
    }

    [Theory]
    [InlineData("auth-invalid")]
    [InlineData("subscribe-error")]
    [InlineData("wrong-ack")]
    [InlineData("malformed")]
    [InlineData("oversize")]
    [InlineData("receive-timeout")]
    [InlineData("send-timeout")]
    public async Task FailedHandshakeNeverBecomesAvailableAndReconnectsWithoutPush(string kind)
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(configure: options => options.TimeoutSeconds = 1, clock: clock);
        harness.Socket.AutoHandshake = false;
        harness.Socket.BlockAuthSend = kind == "send-timeout";
        harness.Factory.ReconnectAutoHandshake = false;
        await harness.Channel.StartAsync(CancellationToken.None);
        await harness.Socket.AuthSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        switch (kind)
        {
            case "auth-invalid": harness.Socket.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = "auth_invalid", message = harness.Secrets.Token })); break;
            case "malformed": harness.Socket.Incoming.Writer.TryWrite("{"); break;
            case "oversize": harness.Socket.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type = "auth_ok", padding = new string('x', 65536) })); break;
            case "subscribe-error":
            case "wrong-ack":
                harness.Socket.Incoming.Writer.TryWrite("{\"type\":\"auth_ok\"}");
                await harness.Socket.SubscribeSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
                harness.Socket.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new { id = kind == "wrong-ack" ? 2 : 1, type = "result", success = kind == "wrong-ack", error = harness.Secrets.Token }));
                break;
        }
        await harness.Socket.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, harness.Factory.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => harness.Factory.Count >= 2);
        Assert.False(harness.Channel.IsAvailable);
        var error = await Record.ExceptionAsync(() => harness.Channel.SendRequestAsync(Notice(), CancellationToken.None));
        Assert.True(error is not null && !error.ToString().Contains(harness.Secrets.Token, StringComparison.Ordinal), "Nyers kézfogáshiba került ki.");
        Assert.Equal(0, harness.Http.Count);
    }

    [Fact]
    public async Task ConnectionTimeoutIsBoundedAndRetriesWithoutTokenLookupOrPush()
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(configure: options => options.TimeoutSeconds = 1, clock: clock);
        harness.Factory.BlockConnect = true;
        await harness.Channel.StartAsync(CancellationToken.None);
        // Sikertelen ConnectAsync nem ad át socketet; a backoff időzítő jelzi a timeout végét.
        await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, harness.Factory.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => harness.Factory.Count >= 2);
        Assert.False(harness.Channel.IsAvailable);
        Assert.Equal(0, harness.Secrets.Calls);
        Assert.Equal(0, harness.Http.Count);
    }

    [Theory]
    [InlineData("http-error")]
    [InlineData("redirect")]
    [InlineData("invalid-body")]
    [InlineData("oversize")]
    [InlineData("http-timeout")]
    [InlineData("caller-cancellation")]
    [InlineData("secret-error")]
    [InlineData("secret-timeout")]
    public async Task SendFailuresAreBoundedSanitizedAndNeverRetried(string kind)
    {
        await using var harness = new Standalone(configure: options => options.TimeoutSeconds = 1);
        await harness.StartAsync();
        switch (kind)
        {
            case "http-error": harness.Http.Status = HttpStatusCode.InternalServerError; harness.Http.ResponseBody = harness.Secrets.Token; break;
            case "redirect": harness.Http.Status = HttpStatusCode.Redirect; break;
            case "invalid-body": harness.Http.ResponseBody = harness.Secrets.Token; break;
            case "oversize": harness.Http.ResponseBody = "[" + new string(' ', 65535) + "]"; break;
            case "http-timeout": case "caller-cancellation": harness.Http.Block = true; break;
            case "secret-error": harness.Secrets.Fail = true; break;
            case "secret-timeout": harness.Secrets.Block = true; break;
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        if (kind == "caller-cancellation") cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        var error = await Record.ExceptionAsync(() => harness.Channel.SendRequestAsync(Notice(), cancellation.Token));
        Assert.True(error is not null && !error.ToString().Contains(harness.Secrets.Token, StringComparison.Ordinal) && error.InnerException is null && error.Data.Count == 0,
            "A küldési hiba nincs biztonságosan szanitizálva.");
        Assert.Equal(kind.StartsWith("secret", StringComparison.Ordinal) ? 0 : 1, harness.Http.Count);
    }

    [Fact]
    public async Task RealProgramExpiresOfflineRejectsNewPushAndNeverRevivesOldCodesAfterReconnect()
    {
        await using var app = new ChannelApp();
        app.Factory.ReconnectAutoHandshake = false;
        using var client = app.CreateClient();
        var channel = Assert.Single(app.Services.GetServices<IApprovalChannel>());
        await WaitAsync(() => channel.IsAvailable);
        using var first = await CallMcpAsync(app, "gate_approval_test", new { text = "első" });
        var firstText = first.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(firstText[(firstText.LastIndexOf(' ') + 1)..], out var id));
        var (oldCode, _) = Codes(app.Http);
        app.Socket.Incoming.Writer.TryWrite("__close");
        await WaitAsync(() => !channel.IsAvailable);
        app.Clock.Now = app.Clock.Now.AddMinutes(15);
        using var offline = await CallMcpAsync(app, "gate_approval_test", new { text = "offline" });
        Assert.True(offline.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        await WaitAsync(() => gate.GetStatus(id)?.State == ApprovalState.Expired);
        Assert.Equal(1, app.Http.Count);
        await WaitAsync(() => app.Factory.Count >= 2 && app.Factory.Last!.AuthSent.Task.IsCompleted);
        var socket = app.Factory.Last!;
        socket.Incoming.Writer.TryWrite("{\"type\":\"auth_ok\"}");
        await socket.SubscribeSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        socket.Incoming.Writer.TryWrite("{\"id\":1,\"type\":\"result\",\"success\":true}");
        await WaitAsync(() => channel.IsAvailable);
        socket.Incoming.Writer.TryWrite(Event(oldCode));
        using var next = await CallMcpAsync(app, "gate_approval_test", new { text = "új" });
        var nextText = next.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(nextText[(nextText.LastIndexOf(' ') + 1)..], out var newId));
        var (_, reject) = Codes(app.Http);
        socket.Incoming.Writer.TryWrite(Event(reject));
        await WaitAsync(() => gate.GetStatus(newId)?.State == ApprovalState.Rejected && app.Http.Count == 3);
        Assert.Equal(0, app.Operation.Count);
        Assert.Equal(ApprovalState.Expired, gate.GetStatus(id)!.State);
    }

    [Fact]
    public async Task RealProgramMcpApprovalExecutesOnceAfterHttpEndsAndAuditsOnlySafeResultsAcrossReconnect()
    {
        await using var app = new ChannelApp();
        using var client = app.CreateClient();
        var channel = Assert.Single(app.Services.GetServices<IApprovalChannel>());
        await WaitAsync(() => channel.IsAvailable);
        using var pending = await CallMcpAsync(app, "gate_approval_test", new { text = "token=" + app.Secrets.Token });
        var text = pending.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(text[(text.LastIndexOf(' ') + 1)..], out var id), "Nincs Pending kérésazonosító.");
        Assert.Equal(0, app.Operation.Count);
        var (approve, reject) = Codes(app.Http);
        app.Socket.Incoming.Writer.TryWrite(Event(approve));
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        await WaitAsync(() => gate.GetStatus(id)?.State == ApprovalState.Executed && app.Http.Count == 2);
        Assert.Equal(1, app.Operation.Count);
        Assert.True(app.Socket.AuthToken == app.Secrets.Token, "A WS-auth nem a titokszolgáltató tokenjét használta.");
        app.Socket.Incoming.Writer.TryWrite("__close");
        await WaitAsync(() => app.Factory.Count >= 2 && channel.IsAvailable);
        app.Factory.Last!.Incoming.Writer.TryWrite(Event(approve));
        app.Factory.Last.Incoming.Writer.TryWrite(Event(reject));
        using var next = await CallMcpAsync(app, "gate_approval_test", new { text = "második" });
        var nextText = next.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(nextText[(nextText.LastIndexOf(' ') + 1)..], out var secondId));
        var (_, secondReject) = Codes(app.Http);
        app.Factory.Last.Incoming.Writer.TryWrite(Event(secondReject));
        await WaitAsync(() => gate.GetStatus(secondId)?.State == ApprovalState.Rejected && app.Http.Count == 4);
        Assert.Equal(1, app.Operation.Count);
        using var status = await CallMcpAsync(app, "gate_approval_status", new { requestId = id.ToString() });
        Assert.True(!status.RootElement.GetRawText().Contains(app.Secrets.Token, StringComparison.Ordinal), "HA-token a státuszban.");
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={app.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Caller, Tool, SanitizedParameters, State, SanitizedError FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        var decisionCount = 0;
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1) == "approval_lifecycle")
            {
                using var parameters = JsonDocument.Parse(reader.GetString(2));
                var data = parameters.RootElement;
                if (data.GetProperty("phase").GetString() == "decision")
                {
                    Assert.True(data.GetProperty("actor").GetString() == "admin" && data.GetProperty("channel").GetString() == "home-assistant", "A HA döntési azonosság nem került helyesen az auditba.");
                    Assert.True(data.GetProperty("decidedAt").TryGetDateTimeOffset(out var decidedAt) && decidedAt == app.Clock.Now, "Hibás döntési idő az auditban.");
                    Assert.True(data.GetProperty("choice").GetString() is "approve" or "reject", "Hibás döntés az auditban.");
                    decisionCount++;
                }
            }
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = reader.IsDBNull(index) ? "" : reader.GetString(index);
                Assert.True(!value.Contains(app.Secrets.Token, StringComparison.Ordinal) && !value.Contains(approve, StringComparison.Ordinal) && !value.Contains(reject, StringComparison.Ordinal), "Titok vagy gombkód a tartós auditban.");
            }
        }
        Assert.Equal(2, decisionCount);
        Assert.True(!app.Logs.Contains(app.Secrets.Token) && !app.Logs.Contains(approve) && !app.Logs.Contains(reject), "Titok vagy gombkód a strukturált diagnosztikában.");
    }

    private static async Task<JsonDocument> CallMcpAsync(ChannelApp app, string name, object arguments)
    {
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.McpToken);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } }, timeout.Token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = body.Split('\n').Last(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        return JsonDocument.Parse(body);
    }

    [Theory]
    [InlineData("missing-user")]
    [InlineData("null-user")]
    [InlineData("wrong-user")]
    [InlineData("data-user-only")]
    [InlineData("wrong-subscription")]
    [InlineData("string-subscription")]
    [InlineData("null-subscription")]
    [InlineData("number-user")]
    [InlineData("null-context")]
    [InlineData("null-data")]
    [InlineData("number-action")]
    [InlineData("wrong-message-type")]
    [InlineData("wrong-event-type")]
    [InlineData("unknown-code")]
    [InlineData("rewritten-prefix")]
    [InlineData("malformed-code")]
    public async Task InvalidEventNeverProducesDecisionOrConsumesTheLegitimateCode(string kind)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var reader = harness.Channel.ReadDecisionsAsync(deadline.Token).GetAsyncEnumerator();
        var first = Notice();
        await harness.Channel.SendRequestAsync(first, deadline.Token);
        var (approve, reject) = Codes(harness.Http);
        var invalid = JsonNode.Parse(Event(approve))!;
        var context = invalid["event"]!["context"]!.AsObject();
        switch (kind)
        {
            case "missing-user": context.Remove("user_id"); break;
            case "null-user": context["user_id"] = null; break;
            case "wrong-user": context["user_id"] = "other"; break;
            case "data-user-only": context.Remove("user_id"); invalid["event"]!["data"]!["user_id"] = "admin"; break;
            case "wrong-subscription": invalid["id"] = 2; break;
            case "string-subscription": invalid["id"] = "1"; break;
            case "null-subscription": invalid["id"] = null; break;
            case "number-user": context["user_id"] = 42; break;
            case "null-context": invalid["event"]!["context"] = null; break;
            case "null-data": invalid["event"]!["data"] = null; break;
            case "number-action": invalid["event"]!["data"]!["action"] = 42; break;
            case "wrong-message-type": invalid["type"] = "result"; break;
            case "wrong-event-type": invalid["event"]!["event_type"] = "state_changed"; break;
            case "unknown-code": invalid["event"]!["data"]!["action"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); break;
            case "rewritten-prefix": invalid["event"]!["data"]!["action"] = "approve_" + reject; break;
            case "malformed-code": invalid["event"]!["data"]!["action"] = "g" + approve[1..]; break;
        }
        harness.Socket.Incoming.Writer.TryWrite(invalid.ToJsonString());
        var second = Notice();
        await harness.Channel.SendRequestAsync(second, deadline.Token);
        var (fence, _) = Codes(harness.Http);
        harness.Socket.Incoming.Writer.TryWrite(Event(fence));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(second.Id, reader.Current.Id);
        harness.Socket.Incoming.Writer.TryWrite(Event(approve));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(first.Id, reader.Current.Id);
    }

    [Fact]
    public async Task DisconnectReconnectsReauthenticatesAndResubscribesWithoutResendingPendingPush()
    {
        var clock = new HeartbeatClock();
        await using var harness = new Standalone(clock: clock);
        await harness.StartAsync();
        var notice = Notice();
        await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
        var (code, _) = Codes(harness.Http);
        harness.Socket.Incoming.Writer.TryWrite("__close");
        await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, harness.Factory.Count);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => harness.Factory.Count >= 2 && harness.Channel.IsAvailable);
        Assert.Equal(1, harness.Http.Count);
        var reconnected = harness.Factory.Last!;
        Assert.True(reconnected.AuthSent.Task.IsCompleted && reconnected.SubscribeSent.Task.IsCompleted);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var reader = harness.Channel.ReadDecisionsAsync(timeout.Token).GetAsyncEnumerator();
        reconnected.Incoming.Writer.TryWrite(Event(code));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(notice.Id, reader.Current.Id);
    }

    [Theory]
    [InlineData(ApprovalState.Executed)]
    [InlineData(ApprovalState.Rejected)]
    [InlineData(ApprovalState.Expired)]
    public async Task OutcomePushIsSanitizedHasNoButtonsAndIsNeverAutomaticallyRetried(ApprovalState state)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var notice = Notice();
        await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
        var (approve, reject) = Codes(harness.Http);
        var outcome = new ApprovalOutcome(notice.Id, state, "token=" + harness.Secrets.Token);
        var error = await Record.ExceptionAsync(() => harness.Channel.SendOutcomeAsync(outcome, CancellationToken.None));
        Assert.True(error is null, "Az eredménypush nem sikerült.");
        Assert.Equal(2, harness.Http.Count);
        Assert.True(!harness.Http.Body!.Contains(approve, StringComparison.Ordinal) && !harness.Http.Body.Contains(reject, StringComparison.Ordinal) &&
            !harness.Http.Body.Contains(harness.Secrets.Token, StringComparison.Ordinal), "Titok vagy gombkód az eredménypushban.");
        using var body = JsonDocument.Parse(harness.Http.Body);
        Assert.True(!body.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("actions", out _), "Az eredmény nem kínálhat új döntési gombokat.");
        _ = await Record.ExceptionAsync(() => harness.Channel.SendOutcomeAsync(outcome, CancellationToken.None));
        Assert.Equal(2, harness.Http.Count);
    }

    [Theory]
    [InlineData(null, null, false, true)]
    [InlineData(null, "device", true, true)]
    [InlineData(null, "other", true, true)]
    [InlineData(null, "number", true, true)]
    [InlineData(null, null, true, true)]
    [InlineData("device", null, false, true)]
    [InlineData("device", "device", true, true)]
    [InlineData("device", "other", true, false)]
    [InlineData("device", "number", true, false)]
    [InlineData("device", "bad value", true, false)]
    [InlineData("device", null, true, false)]
    public async Task DeviceIdentityIsComparedOnlyWhenConfiguredAndPresent(string? expected, string? actual, bool present, bool accepted)
    {
        await using var harness = new Standalone(expected);
        await harness.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var reader = harness.Channel.ReadDecisionsAsync(deadline.Token).GetAsyncEnumerator();
        var first = Notice();
        await harness.Channel.SendRequestAsync(first, deadline.Token);
        var (code, _) = Codes(harness.Http);
        var second = Notice();
        await harness.Channel.SendRequestAsync(second, deadline.Token);
        var (fence, _) = Codes(harness.Http);
        harness.Socket.Incoming.Writer.TryWrite(EventWithDevice(code, actual == "number" ? 42 : actual, present));
        harness.Socket.Incoming.Writer.TryWrite(EventWithDevice(fence, expected, expected is not null));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(accepted ? first.Id : second.Id, reader.Current.Id);
    }

    private static string EventWithDevice(string code, object? device, bool include)
    {
        var data = new Dictionary<string, object?> { ["action"] = code };
        if (include) data["device_id"] = device;
        return JsonSerializer.Serialize(new { id = 1, type = "event", @event = new { event_type = "mobile_app_notification_action", context = new { user_id = "admin" }, data } });
    }

    [Fact]
    public async Task VerifiedActionProducesDecisionBoundToItsCodeAndConsumesBothButtons()
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var reader = harness.Channel.ReadDecisionsAsync(deadline.Token).GetAsyncEnumerator();
        var first = Notice();
        await harness.Channel.SendRequestAsync(first, deadline.Token);
        var (approve, reject) = Codes(harness.Http);
        harness.Socket.Incoming.Writer.TryWrite(Event(approve));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(first.Id, reader.Current.Id);
        Assert.Equal(ApprovalChoice.Approve, reader.Current.Choice);
        Assert.Equal("admin", reader.Current.Actor);
        harness.Socket.Incoming.Writer.TryWrite(Event(approve));
        harness.Socket.Incoming.Writer.TryWrite(Event(reject));
        var second = Notice();
        await harness.Channel.SendRequestAsync(second, deadline.Token);
        var (_, secondReject) = Codes(harness.Http);
        harness.Socket.Incoming.Writer.TryWrite(Event(secondReject));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(second.Id, reader.Current.Id);
        Assert.Equal(ApprovalChoice.Reject, reader.Current.Choice);
    }

    private static ApprovalNotice Notice() => new(Guid.NewGuid(), "Mikaela", "gate_approval_test", RiskLevel.SensitiveControl,
        "próba", DateTimeOffset.UtcNow.AddMinutes(15));

    private static (string Approve, string Reject) Codes(FakeHttp http)
    {
        using var body = JsonDocument.Parse(http.Body!);
        var actions = body.RootElement.GetProperty("data").GetProperty("actions");
        return (actions[0].GetProperty("action").GetString()!, actions[1].GetProperty("action").GetString()!);
    }

    private static string Event(string code) => JsonSerializer.Serialize(new
    {
        id = 1,
        type = "event",
        @event = new { event_type = "mobile_app_notification_action", context = new { user_id = "admin" }, data = new { action = code } }
    });

    [Fact]
    public async Task RequestPushUsesOnlyConfiguredNotifyServiceAndRandomDecisionBoundCodesOutsideHumanText()
    {
        await using var app = new ChannelApp();
        using var client = app.CreateClient();
        var channel = Assert.Single(app.Services.GetServices<IApprovalChannel>());
        await WaitAsync(() => channel.IsAvailable);
        var notice = new ApprovalNotice(Guid.NewGuid(), "Mikaela", "gate_approval_test", RiskLevel.SensitiveControl,
            "token=" + app.Secrets.Token, DateTimeOffset.UtcNow.AddMinutes(15));
        var error = await Record.ExceptionAsync(() => channel.SendRequestAsync(notice, CancellationToken.None));
        Assert.True(error is null, "A validált kérésküldés nem sikerült.");
        Assert.Equal(1, app.Http.Count);
        Assert.True(app.Http.LastUri?.AbsolutePath == "/api/services/notify/mobile_app_test", "Nem a konfigurált notify végpont kapta a kérést.");
        Assert.True(app.Http.Authorization == app.Secrets.Token, "A Bearer nem a titokszolgáltatótól származik.");
        using var payload = JsonDocument.Parse(app.Http.Body!);
        var actions = payload.RootElement.GetProperty("data").GetProperty("actions");
        Assert.Equal(2, actions.GetArrayLength());
        var approve = actions[0].GetProperty("action").GetString()!;
        var reject = actions[1].GetProperty("action").GetString()!;
        Assert.True(actions[0].GetProperty("title").GetString() == "Jóváhagyom" && actions[1].GetProperty("title").GetString() == "Elutasítom", "Hibás gombcímek.");
        Assert.True(approve.Length == 64 && reject.Length == 64 && approve != reject, "A kódok nem független véletlen értékek.");
        Assert.True(!approve.Contains(notice.Id.ToString("N"), StringComparison.Ordinal) && !reject.Contains(notice.Id.ToString("N"), StringComparison.Ordinal), "A kód kérésazonosítóból származik.");
        var message = payload.RootElement.GetProperty("message").GetString()!;
        Assert.True(!message.Contains(app.Secrets.Token, StringComparison.Ordinal) && !message.Contains(approve, StringComparison.Ordinal) && !message.Contains(reject, StringComparison.Ordinal), "Token vagy akciókód az emberi szövegben.");
        Assert.True(message.Contains("Mikaela", StringComparison.Ordinal) && message.Contains("gate_approval_test", StringComparison.Ordinal) && message.Contains("Kockázat: érzékeny vezérlés", StringComparison.Ordinal), "Hiányos összefoglaló.");
    }

    [Fact]
    public async Task AvailabilityRequiresAuthenticationAndSuccessfulMatchingSubscriptionAck()
    {
        await using var app = new ChannelApp();
        app.Socket.AutoHandshake = false;
        using var client = app.CreateClient();
        var channel = Assert.Single(app.Services.GetServices<IApprovalChannel>());
        await app.Socket.AuthSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(channel.IsAvailable);
        Assert.Equal(0, app.Http.Count);
        app.Socket.Incoming.Writer.TryWrite("{\"type\":\"auth_ok\"}");
        await app.Socket.SubscribeSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(channel.IsAvailable);
        Assert.True(app.Socket.SubscriptionType == "mobile_app_notification_action", "Hibás eseménytípusra iratkozott fel.");
        app.Socket.Incoming.Writer.TryWrite("{\"id\":1,\"type\":\"result\",\"success\":true,\"result\":null}");
        await WaitAsync(() => channel.IsAvailable);
        Assert.True(app.Factory.LastUri == new Uri("wss://ha.invalid/api/websocket"), "A WS origin eltér a validált HTTP origintől.");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }


    private sealed class Standalone : IAsyncDisposable
    {
        public FakeSocket Socket { get; } = new();
        public FakeHttp Http { get; } = new();
        public FakeSecrets Secrets { get; } = new();
        public HomeAssistantApprovalChannel Channel { get; }
        public FakeSocketFactory Factory { get; }
        public Standalone(string? expectedDevice = null, Action<HomeAssistantApprovalOptions>? configure = null, TimeProvider? clock = null, ApprovalPushFormatter? formatter = null)
        {
            var options = new HomeAssistantApprovalOptions
            {
                BaseUrl = "https://ha.invalid",
                TokenSecretId = Guid.NewGuid().ToString(),
                NotifyService = "notify.mobile_app_test",
                ExpectedUserId = "admin",
                ExpectedDeviceId = expectedDevice
            };
            configure?.Invoke(options);
            Factory = new FakeSocketFactory(Socket);
            Channel = new HomeAssistantApprovalChannel(Microsoft.Extensions.Options.Options.Create(options), Secrets,
                Factory, Http, clock ?? TimeProvider.System, formatter);
        }
        public async Task StartAsync()
        {
            await Channel.StartAsync(CancellationToken.None);
            await WaitAsync(() => Channel.IsAvailable);
        }
        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Channel.StopAsync(timeout.Token);
            Channel.Dispose();
        }
    }

    private sealed class ChannelApp : WebApplicationFactory<Program>
    {
        private readonly string baseUrl;
        private readonly bool allowHttp;
        private readonly string directory = Directory.CreateTempSubdirectory("agent-gate-ha-approval-").FullName;
        public string AuditPath => Path.Combine(directory, "audit.db");
        public string McpToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public ProbeOperation Operation { get; } = new();
        public CaptureLogs Logs { get; } = new();
        public MutableClock Clock { get; } = new();
        public FakeSocket Socket { get; } = new();
        public FakeHttp Http { get; } = new();
        public FakeSecrets Secrets { get; } = new();
        public FakeSocketFactory Factory { get; }

        public ChannelApp(string baseUrl = "https://ha.invalid", bool allowHttp = false)
        {
            this.baseUrl = baseUrl;
            this.allowHttp = allowHttp;
            Factory = new FakeSocketFactory(Socket);
            Operation.Result = "token=" + Secrets.Token;
        }
        public IAuditStore? AuditOverride { get; set; }
        public ApprovalPushFormatter? FormatterOverride { get; set; }
        public int GateSendTimeoutSeconds { get; set; } = 5;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var values = new Dictionary<string, string?>
            {
                ["Secrets:SelfTestSecretId"] = null,
                ["Audit:DatabasePath"] = AuditPath,
                ["Audit:Caller"] = "Mikaela",
                ["Approval:SendTimeoutSeconds"] = GateSendTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["KillSwitch:Path"] = Path.Combine(directory, "kill-switch.json"),
                ["HomeAssistantApproval:BaseUrl"] = baseUrl,
                ["HomeAssistantApproval:AllowHttp"] = allowHttp.ToString(),
                ["HomeAssistantApproval:TokenSecretId"] = Guid.NewGuid().ToString(),
                ["HomeAssistantApproval:NotifyService"] = "notify.mobile_app_test",
                ["HomeAssistantApproval:ExpectedUserId"] = "admin"
            };
            File.WriteAllText(Path.Combine(directory, "kill-switch.json"), "{\"Enabled\":false}");
            foreach (var (key, value) in values) builder.UseSetting(key, value);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(values));
            builder.ConfigureTestServices(services =>
            {

                services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
                services.PostConfigure<McpAuthenticationOptions>(options => options.Token = McpToken);
                services.RemoveAll<IApprovalOperationHandler>();
                services.AddSingleton<IApprovalOperationHandler>(Operation);
                if (AuditOverride is not null) services.AddSingleton<IAuditStore>(AuditOverride);
                if (FormatterOverride is not null) services.AddSingleton(FormatterOverride);
                services.AddSingleton<ISecretProvider>(Secrets);
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<IApprovalWebSocketFactory>(Factory);
                services.AddSingleton<IApprovalHttpHandlerFactory>(Http);
            });
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class FailPendingStore(string canary) : IAuditStore
    {
        public Task CompletePendingAsync(Guid callId, string state, string? sanitizedError, CancellationToken cancellationToken) => Task.CompletedTask;
        public Guid RequestId { get; private set; }
        public Task BeginAsync(AuditCall call, CancellationToken cancellationToken)
        {
            if (call.Tool == "gate_approval_test") RequestId = call.CallId;
            return Task.CompletedTask;
        }
        public Task CompleteAsync(Guid callId, string state, double durationMilliseconds, string? sanitizedError, CancellationToken cancellationToken)
        {
            if (state == "Pending") throw new InvalidOperationException(canary);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public DateTimeOffset Now { get => new(Interlocked.Read(ref ticks), TimeSpan.Zero); set => Interlocked.Exchange(ref ticks, value.UtcTicks); }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class HeartbeatClock : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ManualTimer> timers = [];
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (sync) return ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public int ActiveTimers { get { lock (sync) return timers.Count(timer => timer.Due is not null); } }
        public bool HasTimerDueIn(TimeSpan delay)
        {
            lock (sync) return timers.Any(timer => timer.Due is { } due && due - ticks == delay.Ticks);
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
                ready = timers.Where(timer => timer.Due is { } due && due <= ticks).ToList();
                foreach (var timer in ready) timer.Due = timer.Period > TimeSpan.Zero ? ticks + timer.Period.Ticks : null;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class ManualTimer(HeartbeatClock owner, TimerCallback callback, object? state) : ITimer
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
            public void Dispose()
            {
                lock (owner.sync) { disposed = true; Due = null; owner.timers.Remove(this); }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ProbeOperation : IApprovalOperationHandler
    {
        public string Tool => "gate_approval_test";
        public RiskLevel Risk => RiskLevel.SensitiveControl;
        public string Result { get; set; } = "végrehajtva";
        public bool Fail { get; set; }
        public int Count;
        public Dictionary<string, string?> ExtraParameters { get; } = [];
        public PreparedOperation Prepare(IReadOnlyDictionary<string, JsonElement> arguments)
        {
            var operation = new ApprovalTestOperation().Prepare(arguments);
            return new PreparedOperation(Tool, Risk, operation.Parameters.Concat(ExtraParameters).ToDictionary(pair => pair.Key, pair => pair.Value));
        }
        public Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Count);
            if (Fail) throw new InvalidOperationException(Result, new Exception(Result));
            return Task.FromResult(Result);
        }
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> entries = new();
        public bool Contains(string value) => entries.Any(entry => entry.Contains(value, StringComparison.Ordinal));
        public ILogger CreateLogger(string categoryName) => new Capture(entries);
        public void Dispose() { }
        private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull { entries.Enqueue("scope:" + State(state)); return null; }
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(formatter(state, exception) + State(state) + exception);
            private static string State<TState>(TState state) => state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(";", pairs.Select(pair => pair.Key + "=" + pair.Value)) : state?.ToString() ?? "";
        }
    }

    private sealed class FakeSecrets : ISecretProvider
    {
        public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public int Calls;
        public bool Fail { get; set; }
        public bool Block { get; set; }
        public Task? BlockedTask { get; set; }
        public bool BlockSynchronously { get; set; }
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> GetSecretAsync(Guid secretId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            try
            {
                if (BlockedTask is not null)
                {
                    if (BlockSynchronously) BlockedTask.GetAwaiter().GetResult();
                    else await BlockedTask;
                }
                if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (Fail) throw new InvalidOperationException(Token, new Exception(Token));
                return Token;
            }
            finally { Finished.TrySetResult(); }
        }
    }

    private sealed class FakeHttp : HttpMessageHandler, IApprovalHttpHandlerFactory
    {
        public int Count;
        public Uri? LastUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        public bool Block { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = "[]";
        public Task? DisposeRelease { get; set; }
        public TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Release { get; set; }
        public TaskCompletionSource BlockingEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpMessageHandler CreateHandler() => this;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            LastUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.Parameter;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Release is not null) { BlockingEntered.TrySetResult(); await Release.WaitAsync(cancellationToken); }
            if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
            var response = new HttpResponseMessage(Status) { Content = DisposeRelease is { } disposeRelease ? new BarrierContent(ResponseBody, disposeRelease, DisposeEntered) : new StringContent(ResponseBody) };
            if (Status == HttpStatusCode.Redirect) response.Headers.Location = new Uri("https://other.invalid/");
            return response;
        }
    }

    private sealed class BarrierContent(string body, Task release, TaskCompletionSource entered) : StringContent(body)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) { entered.TrySetResult(); release.GetAwaiter().GetResult(); }
            base.Dispose(disposing);
        }
    }

    private sealed class FakeSocketFactory(FakeSocket first) : IApprovalWebSocketFactory
    {
        public Uri? LastUri { get; private set; }
        public FakeSocket? Last { get; private set; }
        public int Count;
        public bool ReconnectAutoHandshake { get; set; } = true;
        public bool BlockConnect { get; set; }
        public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            LastUri = uri;
            Last = Interlocked.Increment(ref Count) == 1 ? first : new FakeSocket { AutoHandshake = ReconnectAutoHandshake };
            if (BlockConnect) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Last;
        }
    }

    private sealed class FakeSocket : WebSocket
    {
        private WebSocketState state = WebSocketState.Open;
        private byte[]? pending;
        private int offset;
        public bool AutoHandshake { get; set; } = true;
        public bool BlockAuthSend { get; set; }
        public bool AutoPong { get; set; } = true;
        public bool BlockPingSend { get; set; }
        public Task? FragmentRelease { get; set; }
        public TaskCompletionSource FragmentEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentQueue<long> PingIds { get; } = new();
        public int ReceiveCalls;
        public int ActiveReceives;
        public int MaxActiveReceives;
        public int ActiveSends;
        public int MaxActiveSends;
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<string> Incoming { get; } = Channel.CreateUnbounded<string>();
        public TaskCompletionSource AuthSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SubscribeSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? SubscriptionType { get; private set; }
        public string? AuthToken { get; private set; }
        public FakeSocket() => Incoming.Writer.TryWrite("{\"type\":\"auth_required\"}");
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() { state = WebSocketState.Closed; Disposed.TrySetResult(); }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { state = WebSocketState.Closed; return Task.CompletedTask; }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state != WebSocketState.Open) throw new InvalidOperationException("A tesztkapcsolat nem nyitott.");
            var active = Interlocked.Increment(ref ActiveSends);
            MaxActiveSends = Math.Max(MaxActiveSends, active);
            try
            {
                using var message = JsonDocument.Parse(buffer.AsMemory());
                var type = message.RootElement.GetProperty("type").GetString();
                if (type == "auth")
                {
                    AuthToken = message.RootElement.GetProperty("access_token").GetString();
                    AuthSent.TrySetResult();
                    if (BlockAuthSend) await Task.Delay(Timeout.Infinite, cancellationToken);
                    if (AutoHandshake) Incoming.Writer.TryWrite("{\"type\":\"auth_ok\"}");
                }
                if (type == "subscribe_events")
                {
                    SubscriptionType = message.RootElement.GetProperty("event_type").GetString();
                    SubscribeSent.TrySetResult();
                    if (AutoHandshake) Incoming.Writer.TryWrite("{\"id\":1,\"type\":\"result\",\"success\":true}");
                }
                if (type == "ping")
                {
                    var id = message.RootElement.GetProperty("id").GetInt64();
                    PingIds.Enqueue(id);
                    if (BlockPingSend) await Task.Delay(Timeout.Infinite, cancellationToken);
                    if (AutoPong) Incoming.Writer.TryWrite(JsonSerializer.Serialize(new { id, type = "pong" }));
                }
            }
            finally { Interlocked.Decrement(ref ActiveSends); }
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ReceiveCalls);
            var active = Interlocked.Increment(ref ActiveReceives);
            MaxActiveReceives = Math.Max(MaxActiveReceives, active);
            try
            {
                if (pending is null)
                {
                    var input = await Incoming.Reader.ReadAsync(cancellationToken);
                    if (input == "__close")
                    {
                        state = WebSocketState.CloseReceived;
                        return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
                    }
                    pending = Encoding.UTF8.GetBytes(input);
                    offset = 0;
                }
                if (offset > 0 && FragmentRelease is { } release)
                {
                    FragmentEntered.TrySetResult();
                    await release.WaitAsync(cancellationToken);
                }
                var count = Math.Min(buffer.Count, pending.Length - offset);
                pending.AsSpan(offset, count).CopyTo(buffer.AsSpan());
                offset += count;
                var end = offset == pending.Length;
                if (end) pending = null;
                return new WebSocketReceiveResult(count, WebSocketMessageType.Text, end);
            }
            finally { Interlocked.Decrement(ref ActiveReceives); }
        }
    }
}
