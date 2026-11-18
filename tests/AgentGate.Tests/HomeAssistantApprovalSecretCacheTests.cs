using System.Net;
using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Approval.HomeAssistant;
using AgentGate.Secrets;
using Microsoft.Extensions.Options;
using Xunit;
using static AgentGate.Tests.CachedSecretProviderTests;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedNotificationUnauthorizedRefreshesNextSeparateRequestWithoutRetry(bool outcome)
    {
        await using var harness = new CachedChannel();
        var old = await harness.Cache.GetSecretLeaseAsync(harness.Id);
        await harness.StartAsync();
        var notice = Notice() with { Summary = old.Value };
        if (outcome) await harness.Channel.SendRequestAsync(notice, default);
        var initialPosts = harness.Http.Count;
        var fresh = NewValue();
        harness.Raw.Read = (_, _) => Task.FromResult(fresh);
        harness.Http.Status = HttpStatusCode.Unauthorized;
        harness.Http.ResponseBody = old.Value;
        var error = await Record.ExceptionAsync(() => outcome
            ? harness.Channel.SendOutcomeAsync(new ApprovalOutcome(notice.Id, ApprovalState.Executed, old.Value), default)
            : harness.Channel.SendRequestAsync(notice, default));
        Assert.True(error is InvalidOperationException && error.InnerException is null && error.Data.Count == 0 &&
            error.Message == (outcome ? "Az eredményértesítés nem küldhető." : "A jóváhagyási kérés nem küldhető.") &&
            !error.ToString().Contains(old.Value, StringComparison.Ordinal), "Nem biztonságos notify 401-hiba.");
        Assert.Equal(initialPosts + 1, harness.Http.Count);
        Assert.Equal(1, harness.Raw.Calls);
        Assert.True(!harness.Http.Body!.Contains(old.Value, StringComparison.Ordinal), "A notify törzse ismert titkot tartalmaz.");
        harness.Http.Status = HttpStatusCode.OK;
        harness.Http.ResponseBody = "[]";
        await harness.Channel.SendRequestAsync(Notice() with { Summary = fresh }, default);
        Assert.Equal(initialPosts + 2, harness.Http.Count);
        Assert.Equal(2, harness.Raw.Calls);
        Assert.True(harness.Http.Authorization == fresh, "A következő külön kérés visszavont tokent használt.");
        Assert.True(!harness.Http.Body!.Contains(fresh, StringComparison.Ordinal), "Az új token a notify törzsébe került.");
        Assert.NotSame(old, await harness.Cache.GetSecretLeaseAsync(harness.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedNotificationLateUnauthorizedCannotEvictNewLease(bool outcome)
    {
        await using var harness = new CachedChannel();
        var old = await harness.Cache.GetSecretLeaseAsync(harness.Id);
        await harness.StartAsync();
        var notice = Notice();
        if (outcome) await harness.Channel.SendRequestAsync(notice, default);
        var initialPosts = harness.Http.Count;
        var release = Signal();
        harness.Http.Release = release.Task;
        harness.Http.Status = HttpStatusCode.Unauthorized;
        var pending = outcome
            ? harness.Channel.SendOutcomeAsync(new ApprovalOutcome(notice.Id, ApprovalState.Executed, null), default)
            : harness.Channel.SendRequestAsync(notice, default);
        try
        {
            await harness.Http.BlockingEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            harness.Cache.Invalidate(harness.Id, old);
            // Azonos szövegű, de új generáció sem törölhető egy régi kérés 401-ével.
            harness.Raw.Read = (_, _) => Task.FromResult(old.Value);
            var fresh = await harness.Cache.GetSecretLeaseAsync(harness.Id);
            release.SetResult();
            var error = await Record.ExceptionAsync(() => pending);
            Assert.True(error is InvalidOperationException && !error.ToString().Contains(old.Value, StringComparison.Ordinal), "Nem biztonságos késői notify-hiba.");
            Assert.Same(fresh, await harness.Cache.GetSecretLeaseAsync(harness.Id));
            Assert.Equal(2, harness.Raw.Calls);
            Assert.Equal(initialPosts + 1, harness.Http.Count);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("auth_invalid", true)]
    [InlineData("unexpected", false)]
    public async Task CachedSocketOnlyExplicitAuthInvalidRefreshesExistingReconnect(string type, bool invalidated)
    {
        var clock = new HeartbeatClock();
        await using var harness = new CachedChannel(clock);
        var old = await harness.Cache.GetSecretLeaseAsync(harness.Id);
        var freshValue = NewValue();
        harness.Raw.Read = (_, _) => Task.FromResult(freshValue);
        harness.Socket.AutoHandshake = false;
        await harness.Channel.StartAsync(default);
        await harness.Socket.AuthSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(harness.Socket.AuthToken == old.Value, "A kézfogás nem a cache-generációt használta.");
        harness.Socket.Incoming.Writer.TryWrite(JsonSerializer.Serialize(new { type, message = old.Value }));
        await harness.Socket.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitAsync(() => clock.HasTimerDueIn(TimeSpan.FromSeconds(1)));
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => harness.Channel.IsAvailable);
        Assert.Equal(2, harness.Factory.Count);
        Assert.Equal(invalidated ? 2 : 1, harness.Raw.Calls);
        Assert.Equal(0, harness.Http.Count);
        Assert.True(harness.Factory.Last!.AuthToken == (invalidated ? freshValue : old.Value), "A reconnect hibás tokengenerációt használt.");
        var current = await harness.Cache.GetSecretLeaseAsync(harness.Id);
        Assert.True(ReferenceEquals(old, current) != invalidated, "Nem célzott WS-visszavonás.");
    }

    [Fact]
    public async Task CachedSocketLateAuthInvalidCannotEvictNewGeneration()
    {
        await using var harness = new CachedChannel();
        var old = await harness.Cache.GetSecretLeaseAsync(harness.Id);
        harness.Socket.AutoHandshake = false;
        await harness.Channel.StartAsync(default);
        await harness.Socket.AuthSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        harness.Cache.Invalidate(harness.Id, old);
        var fresh = await harness.Cache.GetSecretLeaseAsync(harness.Id);
        harness.Socket.Incoming.Writer.TryWrite("{\"type\":\"auth_invalid\"}");
        await harness.Socket.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(fresh, await harness.Cache.GetSecretLeaseAsync(harness.Id));
        Assert.Equal(2, harness.Raw.Calls);
        Assert.Equal(0, harness.Http.Count);
    }

    private sealed class CachedChannel : IAsyncDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Source Raw { get; } = new();
        public CachedSecretProvider Cache { get; }
        public FakeSocket Socket { get; } = new();
        public FakeHttp Http { get; } = new();
        public FakeSocketFactory Factory { get; }
        public HomeAssistantApprovalChannel Channel { get; }
        public CachedChannel(TimeProvider? clock = null)
        {
            clock ??= TimeProvider.System;
            Cache = new CachedSecretProvider(Raw, clock);
            Factory = new FakeSocketFactory(Socket);
            Channel = new HomeAssistantApprovalChannel(Options.Create(new HomeAssistantApprovalOptions
            {
                BaseUrl = "https://ha.invalid", TokenSecretId = Id.ToString(),
                NotifyService = "notify.mobile_app_test", ExpectedUserId = "admin"
            }), Cache, Factory, Http, clock);
        }
        public async Task StartAsync()
        {
            await Channel.StartAsync(default);
            await WaitAsync(() => Channel.IsAvailable);
        }
        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await Channel.StopAsync(deadline.Token);
            Channel.Dispose();
            await Cache.DisposeAsync();
        }
    }
}
