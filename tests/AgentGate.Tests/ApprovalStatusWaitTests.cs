using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AgentGate.Approval;
using AgentGate.Audit;
using AgentGate.Policy;
using AgentGate.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgentGate.Tests;

public sealed class ApprovalStatusWaitTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(121)]
    [InlineData(int.MaxValue)]
    [InlineData(1.5)]
    [InlineData("1")]
    [InlineData("invalid")]
    [InlineData(true)]
    [InlineData(null)]
    public async Task ActualToolRejectsInvalidWaitWithoutCreatingWaiters(object? value)
    {
        await using var fixture = await Fixture.Create();
        var response = await fixture.Status(Guid.NewGuid(), value);
        Assert.True(response.IsError);
        Assert.Equal(0, fixture.Gate.ActiveStatusWaits);
    }

    [Fact]
    public async Task OmittedAndZeroWaitReturnPendingImmediately()
    {
        await using var fixture = await Fixture.Create();
        var id = await fixture.Request();
        var response = await fixture.Client.CallToolAsync("gate_approval_status", new Dictionary<string, object?> { ["requestId"] = id.ToString("D") });
        Assert.Equal("várakozik", Text(response));
        Assert.Equal("várakozik", Text(await fixture.Status(id, 0)));
        Assert.Equal(0, fixture.Gate.ActiveStatusWaits);
        Assert.Equal(0, fixture.Handler.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutReturnsCurrentPendingOrApprovedWithoutExtendingOrExecuting(bool approved)
    {
        await using var fixture = await Fixture.Create();
        fixture.Handler.Block = true;
        var id = await fixture.Request();
        var expiry = fixture.Channel.Notices.Last().ExpiresAt;
        if (approved)
        {
            fixture.Channel.Decide(id, ApprovalChoice.Approve);
            await fixture.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        var before = System.Diagnostics.Stopwatch.StartNew();
        var response = await fixture.Status(id, 1);
        Assert.InRange(before.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(3));
        Assert.Contains(approved ? "jóváhagyva;" : "várakozik", Text(response));
        Assert.Equal(approved ? ApprovalState.Approved : ApprovalState.Pending, fixture.Gate.GetStatus(id)!.State);
        Assert.Equal(approved ? 1 : 0, fixture.Handler.Executions);
        Assert.Equal(expiry, fixture.Channel.Notices.Last().ExpiresAt);
        Assert.Equal(0, fixture.Gate.ActiveStatusWaits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovalWakesFirstWaitAndSeparateResultWaitNeverRepeatsEffect(bool failure)
    {
        await using var fixture = await Fixture.Create();
        fixture.Handler.Block = true;
        fixture.Handler.Fail = failure;
        var id = await fixture.Request();
        var decision = fixture.Status(id, 120);
        await Until(() => fixture.Gate.ActiveStatusWaits == 1);
        fixture.Channel.Decide(id, ApprovalChoice.Approve);
        var approved = Text(await decision.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("jóváhagyva;", approved);
        Assert.DoesNotContain("és végrehajtva", approved);
        await fixture.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = fixture.Status(id, 120);
        await Until(() => fixture.Gate.ActiveStatusWaits == 1);
        Assert.False(result.IsCompleted);
        fixture.Handler.Release.TrySetResult();
        Assert.Contains(failure ? "sikertelen:" : "jóváhagyva és végrehajtva:", Text(await result.WaitAsync(TimeSpan.FromSeconds(3))));
        fixture.Channel.Decide(id, ApprovalChoice.Approve);
        Assert.Contains(failure ? "sikertelen:" : "jóváhagyva és végrehajtva:", Text(await fixture.Status(id, 120).WaitAsync(TimeSpan.FromSeconds(1))));
        Assert.Equal(1, fixture.Handler.Executions);
        Assert.Equal(0, fixture.Gate.ActiveStatusWaits);
    }

    [Fact]
    public async Task AlreadyRejectedAndUnknownAreImmediate()
    {
        await using var fixture = await Fixture.Create();
        var id = await fixture.Request();
        fixture.Channel.Decide(id, ApprovalChoice.Reject);
        await Until(() => fixture.Gate.GetStatus(id)?.State == ApprovalState.Rejected);
        Assert.Equal("elutasítva", Text(await fixture.Status(id, 120).WaitAsync(TimeSpan.FromSeconds(1))));
        Assert.Equal("A kérés nem található.", Text(await fixture.Status(Guid.NewGuid(), 120).WaitAsync(TimeSpan.FromSeconds(1))));
        Assert.Equal(0, fixture.Gate.ActiveStatusWaits);
    }

    [Fact]
    public async Task ForeignOwnerIsIndistinguishableFromUnknownAndClientCannotOverrideIt()
    {
        await using var fixture = await Fixture.Create();
        var id = await fixture.Request();
        fixture.App.Services.GetRequiredService<IOptions<AuditOptions>>().Value.Caller = "other-owner";
        var result = await fixture.Client.CallToolAsync("gate_approval_status", new Dictionary<string, object?>
        { ["requestId"] = id.ToString("D"), ["wait_seconds"] = 120, ["caller"] = "test-caller" });
        Assert.Equal("A kérés nem található.", Text(result));
        Assert.Equal(Text(result), Text(await fixture.Status(Guid.NewGuid(), 120)));
        Assert.Equal(0, fixture.Gate.ActiveStatusWaits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualMcpCallerCancellationReleasesStatusSlotAndTimers(bool approved)
    {
        await using var fixture = await Fixture.Create();
        fixture.Handler.Block = true;
        var id = await fixture.Request();
        if (approved)
        {
            fixture.Channel.Decide(id, ApprovalChoice.Approve);
            await fixture.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        var baseline = fixture.Clock.ActiveTimers;
        using var stop = new CancellationTokenSource();
        var wait = fixture.Status(id, 120, stop.Token);
        await Until(() => fixture.Gate.ActiveStatusWaits == 1);
        stop.Cancel();
        var error = await Record.ExceptionAsync(() => wait);
        Assert.True(error is OperationCanceledException or HttpRequestException { StatusCode: (System.Net.HttpStatusCode)499 });
        await Until(() => fixture.Gate.ActiveStatusWaits == 0 && fixture.Clock.ActiveTimers == baseline);
        Assert.Equal(approved ? 1 : 0, fixture.Handler.Executions);
    }

    [Theory]
    [InlineData("{\"Enabled\":true}")]
    [InlineData("invalid")]
    public async Task KillSwitchOrUnreadableSwitchUnblocksAndCleansWait(string content)
    {
        await using var fixture = await Fixture.Create();
        var id = await fixture.Request();
        var baseline = fixture.Clock.ActiveTimers;
        var wait = fixture.Status(id, 120);
        await Until(() => fixture.Gate.ActiveStatusWaits == 1);
        await File.WriteAllTextAsync(fixture.App.KillSwitchPath, content);
        Assert.Equal("várakozik", Text(await wait.WaitAsync(TimeSpan.FromSeconds(2))));
        Assert.Equal(ApprovalState.Pending, fixture.Gate.GetStatus(id)!.State);
        await Until(() => fixture.Gate.ActiveStatusWaits == 0 && fixture.Clock.ActiveTimers == baseline);
        Assert.Equal(0, fixture.Handler.Executions);
    }

    [Fact]
    public async Task HostStopCancelsWaitAndReleasesResources()
    {
        await using var fixture = await Fixture.Create();
        var id = await fixture.Request();
        var wait = fixture.Gate.GetStatusAsync(id, 120);
        await Until(() => fixture.Gate.ActiveStatusWaits == 1);
        await fixture.Gate.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(0, fixture.Gate.ActiveStatusWaits);
        Assert.Equal(0, fixture.Clock.ActiveTimers);
    }

    [Fact]
    public async Task ConcurrentWaitsAreBoundedAndCapacityIsRecoveredAfterCancellation()
    {
        await using var fixture = await Fixture.Create();
        var id = await fixture.Request();
        var baseline = fixture.Clock.ActiveTimers;
        using var stop = new CancellationTokenSource();
        var waits = Enumerable.Range(0, ApprovalGate.MaximumStatusWaits).Select(_ => fixture.Gate.GetStatusAsync(id, 120, stop.Token)).ToArray();
        await Until(() => fixture.Gate.ActiveStatusWaits == ApprovalGate.MaximumStatusWaits);
        Assert.True((await fixture.Status(id, 120)).IsError);
        Assert.Equal("várakozik", Text(await fixture.Status(id, 0)));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(waits));
        await Until(() => fixture.Gate.ActiveStatusWaits == 0 && fixture.Clock.ActiveTimers == baseline);
        fixture.Channel.Decide(id, ApprovalChoice.Reject);
        Assert.Equal("elutasítva", Text(await fixture.Status(id, 120).WaitAsync(TimeSpan.FromSeconds(2))));
    }

    [Fact]
    public async Task DecisionBetweenSnapshotAndAwaitCannotBeLost()
    {
        var source = new BarrierSwitch();
        await using var fixture = await Fixture.Create(services => services.AddSingleton<IKillSwitchStateSource>(source));
        var id = await fixture.Request();
        source.Block = true;
        var wait = fixture.Status(id, 120);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Channel.Decide(id, ApprovalChoice.Reject);
        await Until(() => fixture.Gate.GetStatus(id)?.State == ApprovalState.Rejected);
        source.Release.TrySetResult();
        Assert.Equal("elutasítva", Text(await wait.WaitAsync(TimeSpan.FromSeconds(1))));
        Assert.Equal(0, fixture.Gate.ActiveStatusWaits);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    [Fact]
    public async Task RepeatedDisposeAndLateHostStopAreSafe()
    {
        await using var fixture = await Fixture.Create();
        fixture.Gate.Dispose();
        fixture.Gate.Dispose();
        await fixture.Gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExpiryWakesPendingWaitAndRemainsTerminal()
    {
        await using var fixture = await Fixture.Create();
        var id = await fixture.Request();
        var wait = fixture.Status(id, 120);
        await Until(() => fixture.Gate.ActiveStatusWaits == 1);
        fixture.Clock.Now = fixture.Channel.Notices.Last().ExpiresAt.AddSeconds(1);
        Assert.Equal("lejárt", Text(await wait.WaitAsync(TimeSpan.FromSeconds(3))));
        fixture.Channel.Decide(id, ApprovalChoice.Approve);
        Assert.Equal("lejárt", Text(await fixture.Status(id, 120).WaitAsync(TimeSpan.FromSeconds(1))));
        Assert.Equal(0, fixture.Handler.Executions);
        Assert.Equal(0, fixture.Gate.ActiveStatusWaits);
    }

    [Fact]
    public async Task ActualToolSchemaDocumentsBoundedOptionalWaitAndContinuation()
    {
        await using var fixture = await Fixture.Create();
        var tool = Assert.Single(await fixture.Client.ListToolsAsync(), item => item.Name == "gate_approval_status");
        var schema = tool.JsonSchema;
        var wait = schema.GetProperty("properties").GetProperty("wait_seconds");
        Assert.Equal("integer", wait.GetProperty("type").GetString());
        Assert.Equal(0, wait.GetProperty("default").GetInt32());
        Assert.Equal(0, wait.GetProperty("minimum").GetInt32());
        Assert.Equal(120, wait.GetProperty("maximum").GetInt32());
        Assert.DoesNotContain("wait_seconds", schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("jóváhagyás", tool.Description);
        Assert.Contains("végrehajt", tool.Description);
        Assert.Contains("újra", tool.Description);
    }

    [Fact]
    public async Task PendingStatusWaitDoesNotReturnBeforeDecision()
    {
        await using var fixture = await Fixture.Create();
        var id = await fixture.Request();
        using var cancellation = new CancellationTokenSource();
        var wait = fixture.Status(id, 120, cancellation.Token);
        await Task.Delay(150);
        Assert.False(wait.IsCompleted);
        fixture.Channel.Decide(id, ApprovalChoice.Reject);
        Assert.Contains("elutasítva", Text(await wait.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal(0, fixture.Handler.Executions);
    }

    internal static string Text(CallToolResult result) => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(item => item.Text));

    private sealed class Fixture(McpTestApplication app, McpClient client, TestChannel channel, TestHandler handler, TimerClock clock) : IAsyncDisposable
    {
        public McpTestApplication App => app;
        public McpClient Client => client;
        public TestChannel Channel => channel;
        public TestHandler Handler => handler;
        public TimerClock Clock => clock;
        public ApprovalGate Gate => app.Services.GetRequiredService<ApprovalGate>();
        public static async Task<Fixture> Create(Action<IServiceCollection>? configure = null)
        {
            var channel = new TestChannel();
            var handler = new TestHandler();
            var clock = new TimerClock();
            var app = new McpTestApplication("test-token", services =>
            {
                services.RemoveAll<IApprovalChannel>();
                services.AddSingleton<IApprovalChannel>(channel);
                services.RemoveAll<IApprovalOperationHandler>();
                services.AddSingleton<IApprovalOperationHandler>(handler);
                services.AddSingleton<TimeProvider>(clock);
                configure?.Invoke(services);
            });
            var http = app.CreateClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", "test-token");
            var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http));
            return new(app, client, channel, handler, clock);
        }
        public async Task<Guid> Request()
        {
            _ = Gate;
            var response = await client.CallToolAsync("gate_approval_test", new Dictionary<string, object?> { ["text"] = "teszt" });
            Assert.True(response.IsError != true, Text(response));
            return Channel.Notices.Last().Id;
        }
        public Task<CallToolResult> Status(Guid id, object? wait, CancellationToken cancellationToken = default) =>
            client.CallToolAsync("gate_approval_status", new Dictionary<string, object?> { ["requestId"] = id.ToString("D"), ["wait_seconds"] = wait }, cancellationToken: cancellationToken).AsTask();
        public async ValueTask DisposeAsync() { handler.Release.TrySetResult(); await client.DisposeAsync(); await app.DisposeAsync(); }
    }

    private sealed class TestChannel : IApprovalChannel
    {
        private readonly Channel<ApprovalDecision> decisions = System.Threading.Channels.Channel.CreateUnbounded<ApprovalDecision>();
        public ConcurrentQueue<ApprovalNotice> Notices { get; } = new();
        public string Name => "test";
        public bool IsAvailable => true;
        public Task SendRequestAsync(ApprovalNotice notice, CancellationToken cancellationToken) { Notices.Enqueue(notice); return Task.CompletedTask; }
        public void Decide(Guid id, ApprovalChoice choice) => decisions.Writer.TryWrite(new(id, choice, "tester", DateTimeOffset.UtcNow));
        public async IAsyncEnumerable<ApprovalDecision> ReadDecisionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { await foreach (var item in decisions.Reader.ReadAllAsync(cancellationToken)) yield return item; }
        public Task SendOutcomeAsync(ApprovalOutcome outcome, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestHandler : IApprovalOperationHandler
    {
        public string Tool => "gate_approval_test";
        public AgentGate.Policy.RiskLevel Risk => AgentGate.Policy.RiskLevel.SensitiveControl;
        public bool Block { get; set; }
        public bool Fail { get; set; }
        public int Executions;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PreparedOperation Prepare(IReadOnlyDictionary<string, JsonElement> arguments) =>
            new(Tool, Risk, new Dictionary<string, string?> { ["text"] = "teszt" });
        public async Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Executions);
            Started.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            if (Fail) throw new InvalidOperationException("próbahiba");
            return "kész";
        }
    }

    private sealed class BarrierSwitch : IKillSwitchStateSource
    {
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken)
        {
            if (Block)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return false;
        }
    }

    private sealed class TimerClock : TimeProvider
    {
        private int activeTimers;
        public int ActiveTimers => Volatile.Read(ref activeTimers);
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref activeTimers);
            return new CountedTimer(System.CreateTimer(callback, state, dueTime, period), () => Interlocked.Decrement(ref activeTimers));
        }
        private sealed class CountedTimer(ITimer inner, Action disposed) : ITimer
        {
            private int isDisposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
            public void Dispose() { inner.Dispose(); if (Interlocked.Exchange(ref isDisposed, 1) == 0) disposed(); }
            public async ValueTask DisposeAsync() { await inner.DisposeAsync(); if (Interlocked.Exchange(ref isDisposed, 1) == 0) disposed(); }
        }
    }
}
