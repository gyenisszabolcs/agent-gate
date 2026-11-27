using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AgentGate.Approval;
using AgentGate.Audit;
using AgentGate.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class ApprovalStatusRaceTests
{
    [Theory]
    [InlineData("execution", false)]
    [InlineData("execution", true)]
    [InlineData("outcome", false)]
    [InlineData("outcome", true)]
    [InlineData("original", false)]
    [InlineData("original", true)]
    public async Task ResultIsPublishedOnlyAfterAllTerminalAudits(string phase, bool fail)
    {
        await using var fixture = new Fixture();
        fixture.Audit.Phase = phase;
        fixture.Audit.Fail = fail;
        var id = await fixture.Request();
        var decision = fixture.Gate.GetStatusAsync(id, 120);
        fixture.Decide(id);
        Assert.Equal(ApprovalState.Approved, (await decision.WaitAsync(TimeSpan.FromSeconds(3)))!.State);
        await fixture.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = fixture.Gate.GetStatusAsync(id, 120);
        fixture.Handler.Release.TrySetResult();
        try
        {
            await fixture.Audit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // A valódi munkás az auditban áll; a státuszlekérdezésnek eközben sem szabad sikert ígérnie.
            await Task.WhenAny(result, Task.Delay(200));
            Assert.False(result.IsCompleted, "A végállapot az audit lezárása előtt kiszivárgott.");
            Assert.Equal(ApprovalState.Approved, fixture.Gate.GetStatus(id)!.State);
            Assert.Empty(fixture.Channel.Outcomes);
        }
        finally { fixture.Audit.Release.TrySetResult(); }
        var final = await result.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(fail ? ApprovalState.Failed : ApprovalState.Executed, final!.State);
        var notice = await fixture.Channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(final, notice);
        if (fail)
        {
            Assert.Contains("nem visszagörgetés", final.Result);
            Assert.DoesNotContain("audit-provider-canary", final.Result);
        }
        fixture.Decide(id);
        Assert.Equal(final, await fixture.Gate.GetStatusAsync(id, 120).WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, fixture.Handler.Executions);
        Assert.Single(fixture.Channel.Outcomes);
        Assert.Equal(0, fixture.Gate.ActiveStatusWaits);
    }

    [Fact]
    public async Task ApprovalBetweenInitialSnapshotAndSubscriptionReturnsDecisionWithoutWaitingForEffect()
    {
        await using var fixture = new Fixture();
        var id = await fixture.Request();
        fixture.Scopes.Arm();
        var wait = Task.Run(() => fixture.Gate.GetStatusAsync(id, 120));
        try
        {
            await fixture.Scopes.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.Decide(id);
            await fixture.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { fixture.Scopes.Release.Set(); }
        Assert.Equal(ApprovalState.Approved, (await wait.WaitAsync(TimeSpan.FromSeconds(1)))!.State);
        Assert.False(fixture.Handler.Release.Task.IsCompleted);
        Assert.Equal(1, fixture.Handler.Executions);
        Assert.Equal(0, fixture.Gate.ActiveStatusWaits);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider services;
        public TestChannel Channel { get; } = new();
        public TestHandler Handler { get; } = new();
        public BarrierAudit Audit { get; } = new();
        public BarrierScopes Scopes { get; }
        public ApprovalGate Gate { get; }
        public Fixture()
        {
            services = new ServiceCollection().AddSingleton<IAuditStore>(Audit)
                .AddSingleton<IKillSwitchStateSource>(new DisabledSwitch())
                .AddSingleton<IApprovalOperationHandler>(Handler).BuildServiceProvider();
            Scopes = new(services.GetRequiredService<IServiceScopeFactory>());
            Gate = new([Channel], [Handler], Options.Create(new AuditOptions()), Options.Create(new ApprovalGateOptions()),
                TimeProvider.System, new InMemoryApprovalStore(TimeProvider.System), Scopes);
        }
        public async Task<Guid> Request()
        {
            await Gate.StartAsync(default);
            var id = Guid.NewGuid();
            Assert.Equal(id, await Gate.RequestAsync(id, Handler.Tool, Handler.Risk, [], new Dictionary<string, JsonElement>(), default));
            await Gate.ConfirmRequestAsync(id, true);
            return id;
        }
        public void Decide(Guid id) => Channel.Decisions.Writer.TryWrite(new(id, ApprovalChoice.Approve, "teszt", DateTimeOffset.UtcNow));
        public async ValueTask DisposeAsync()
        {
            Scopes.Release.Set();
            Audit.Release.TrySetResult();
            Handler.Release.TrySetResult();
            await Gate.StopAsync(default);
            Gate.Dispose();
            await services.DisposeAsync();
            Scopes.Release.Dispose();
        }
    }

    private sealed class BarrierScopes(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        private int armed;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void Arm() => Interlocked.Exchange(ref armed, 1);
        public IServiceScope CreateScope()
        {
            if (Interlocked.Exchange(ref armed, 0) == 1)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            }
            return inner.CreateScope();
        }
    }

    private sealed class BarrierAudit : IAuditStore
    {
        private readonly ConcurrentDictionary<Guid, string> phases = new();
        public string? Phase { get; set; }
        public bool Fail { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task BeginAsync(AuditCall call, CancellationToken cancellationToken)
        {
            using var parameters = JsonDocument.Parse(call.SanitizedParameters);
            phases[call.CallId] = parameters.RootElement.GetProperty("phase").GetString()!;
            return Task.CompletedTask;
        }
        public Task CompleteAsync(Guid callId, string state, double durationMilliseconds, string? sanitizedError, CancellationToken cancellationToken) =>
            Hold(phases[callId], cancellationToken);
        public Task CompletePendingAsync(Guid callId, string state, string? sanitizedError, CancellationToken cancellationToken) => Hold("original", cancellationToken);
        private async Task Hold(string phase, CancellationToken cancellationToken)
        {
            if (phase != Phase) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (Fail) throw new InvalidOperationException("audit-provider-canary");
        }
    }

    private sealed class DisabledSwitch : IKillSwitchStateSource
    {
        public ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class TestHandler : IApprovalOperationHandler
    {
        public string Tool => "gate_approval_test";
        public RiskLevel Risk => RiskLevel.SensitiveControl;
        public int Executions;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PreparedOperation Prepare(IReadOnlyDictionary<string, JsonElement> arguments) => new(Tool, Risk, new Dictionary<string, string?>());
        public async Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Executions);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return "kész";
        }
    }

    private sealed class TestChannel : IApprovalChannel
    {
        public bool IsAvailable => true;
        public string Name => "teszt";
        public Channel<ApprovalDecision> Decisions { get; } = System.Threading.Channels.Channel.CreateUnbounded<ApprovalDecision>();
        public ConcurrentQueue<ApprovalOutcome> Outcomes { get; } = new();
        public TaskCompletionSource<ApprovalOutcome> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendRequestAsync(ApprovalNotice request, CancellationToken cancellationToken) => Task.CompletedTask;
        public async IAsyncEnumerable<ApprovalDecision> ReadDecisionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { await foreach (var decision in Decisions.Reader.ReadAllAsync(cancellationToken)) yield return decision; }
        public Task SendOutcomeAsync(ApprovalOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Enqueue(outcome);
            Finished.TrySetResult(outcome);
            return Task.CompletedTask;
        }
    }
}
