using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AgentGate.Audit;
using AgentGate.Policy;
using AgentGate.Secrets;
using Microsoft.Extensions.Options;

namespace AgentGate.Approval;

public sealed class ApprovalGate(
    IEnumerable<IApprovalChannel> channels,
    IEnumerable<IApprovalOperationHandler> handlers,
    IOptions<AuditOptions> auditOptions,
    IOptions<ApprovalGateOptions> gateOptions,
    TimeProvider clock,
    InMemoryApprovalStore approvals,
    IServiceScopeFactory scopes) : BackgroundService
{
    private readonly IApprovalChannel? channel = channels.SingleOrDefault();
    private readonly ConcurrentDictionary<Guid, PendingOperation> requests = new();
    private readonly Channel<ApprovalOutcome> outcomes = Channel.CreateBounded<ApprovalOutcome>(1024);
    private volatile bool readerHealthy = true;
    private readonly ApprovalChannelWork channelWork = new(clock, TimeSpan.FromSeconds(gateOptions.Value.SendTimeoutSeconds));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        readerHealthy = false;
        await Task.WhenAll(base.StopAsync(cancellationToken), channelWork.StopAsync(cancellationToken));
    }

    public override void Dispose()
    {
        channelWork.Dispose();
        base.Dispose();
    }

    public async Task ConfirmRequestAsync(Guid id, bool committed)
    {
        if (!requests.TryGetValue(id, out var pending))
            return;
        ApprovalOutcome? failed = null;
        lock (pending.Sync)
        {
            if (!committed && !pending.Claimed)
            {
                pending.Claimed = true;
                pending.State = ApprovalState.Failed;
                pending.Result = "A kérés előzetes auditja nem véglegesíthető; nincs végrehajtási engedély.";
                failed = new ApprovalOutcome(id, pending.State, pending.Result);
            }
            pending.Ready.TrySetResult(committed);
        }
        if (failed is not null)
        {
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RecordLifecycleAsync(pending, "failed", null, completion.Token);
            if (channel is not null && !outcomes.Writer.TryWrite(failed))
                readerHealthy = false;
        }
    }

    public ApprovalOutcome? GetStatus(Guid id)
    {
        if (!requests.TryGetValue(id, out var pending) || !string.Equals(pending.Owner, auditOptions.Value.Caller, StringComparison.Ordinal))
            return null;
        lock (pending.Sync)
            return new ApprovalOutcome(id, pending.State, pending.Result);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(ReadDecisionsAsync(stoppingToken), ExpireAsync(stoppingToken), SendOutcomesAsync(stoppingToken));
    }

    private async Task ExpireAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var (id, pending) in requests)
                {
                    lock (pending.Sync)
                    {
                        if (pending.Claimed || !pending.Ready.Task.IsCompletedSuccessfully || !pending.Ready.Task.Result || clock.GetUtcNow() < pending.Notice.ExpiresAt)
                            continue;
                        pending.Claimed = true;
                        pending.State = ApprovalState.Expired;
                    }
                    await RecordLifecycleAsync(pending, "expired", null, stoppingToken);
                    await CompleteOriginalAsync(pending);
                    if (channel is not null)
                        await NotifyAsync(new ApprovalOutcome(id, pending.State, pending.Result), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ReadDecisionsAsync(CancellationToken stoppingToken)
    {
        if (channel is null)
            return;
        try
        {
            await foreach (var decision in channel.ReadDecisionsAsync(stoppingToken))
            {
                if (!requests.TryGetValue(decision.Id, out var pending))
                    continue;
                if (!await pending.Ready.Task.WaitAsync(stoppingToken))
                    continue;
                lock (pending.Sync)
                {
                    if (pending.Claimed)
                        continue;
                    pending.Claimed = true;
                }
                if (clock.GetUtcNow() >= pending.Notice.ExpiresAt)
                {
                    lock (pending.Sync)
                        pending.State = ApprovalState.Expired;
                    await RecordLifecycleAsync(pending, "expired", null, stoppingToken);
                    await CompleteOriginalAsync(pending);
                    await NotifyAsync(new ApprovalOutcome(decision.Id, pending.State, pending.Result), stoppingToken);
                    continue;
                }
                if (!await RecordLifecycleAsync(pending, "decision", decision, stoppingToken))
                {
                    await CompleteOriginalAsync(pending);
                    await NotifyAsync(new ApprovalOutcome(decision.Id, pending.State, pending.Result), stoppingToken);
                    continue;
                }
                if (decision.Choice != ApprovalChoice.Approve)
                {
                    lock (pending.Sync)
                        pending.State = ApprovalState.Rejected;
                    await CompleteOriginalAsync(pending);
                    await NotifyAsync(new ApprovalOutcome(decision.Id, pending.State, pending.Result), stoppingToken);
                    continue;
                }
                using var scope = scopes.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IAuditStore>();
                var callId = Guid.NewGuid();
                var auditStarted = false;
                var completionAttempted = false;
                try
                {
                    await store.BeginAsync(new AuditCall(callId, clock.GetUtcNow(), SecretRedactor.RedactText(pending.Owner)!,
                        pending.Operation.Tool, pending.Operation.Risk, JsonSerializer.Serialize(new { requestId = decision.Id, phase = "execution" })), stoppingToken);
                    auditStarted = true;
                    var enabled = await scope.ServiceProvider.GetRequiredService<IKillSwitchStateSource>().IsEnabledAsync(stoppingToken);
                    if (!KillSwitchPolicy.IsAllowed(pending.Operation.Risk, enabled))
                        throw new InvalidOperationException();
                    if (!TryAuthorize(pending))
                    {
                        lock (pending.Sync)
                            pending.State = ApprovalState.Expired;
                        completionAttempted = true;
                        await store.CompleteAsync(callId, "Failed", 0, "A jóváhagyási kérés lejárt.", stoppingToken);
                        await RecordLifecycleAsync(pending, "expired", null, stoppingToken);
                        await CompleteOriginalAsync(pending);
                        await NotifyAsync(new ApprovalOutcome(decision.Id, pending.State, pending.Result), stoppingToken);
                        continue;
                    }
                    var handler = scope.ServiceProvider.GetServices<IApprovalOperationHandler>().Single(item => item.Tool == pending.Operation.Tool && item.Risk == pending.Operation.Risk);
                    var result = await handler.ExecuteAsync(pending.Operation, stoppingToken);
                    lock (pending.Sync)
                    {
                        pending.State = ApprovalState.Executed;
                        pending.Result = SecretRedactor.RedactText(result);
                    }
                    completionAttempted = true;
                    using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await store.CompleteAsync(callId, "Succeeded", 0, null, completion.Token);
                }
                catch (Exception)
                {
                    lock (pending.Sync)
                    {
                        pending.State = ApprovalState.Failed;
                        pending.Result = "A jóváhagyott művelet vagy az audit lezárása sikertelen; ez nem visszagörgetés.";
                    }
                    if (auditStarted && !completionAttempted)
                    {
                        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try { await store.CompleteAsync(callId, "Failed", 0, pending.Result, completion.Token); }
                        catch (Exception) { /* A tartós Started sor nem jelent visszagörgetést. */ }
                    }
                }
                await RecordLifecycleAsync(pending, "outcome", null, stoppingToken);
                await CompleteOriginalAsync(pending);
                await NotifyAsync(new ApprovalOutcome(decision.Id, pending.State, pending.Result), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception)
        {
            // A csatorna nyers hibáját a hosted-service diagnosztikája sem kaphatja meg.
        }
        finally { readerHealthy = false; }
    }

    private bool TryAuthorize(PendingOperation pending)
    {
        if (clock.GetUtcNow() >= pending.Notice.ExpiresAt)
            return false;
        var hash = pending.Operation.ComputeHash(pending.Owner);
        if (!string.Equals(hash, pending.Hash, StringComparison.Ordinal))
            throw new InvalidOperationException();
        ApprovalRequest permission;
        try { permission = approvals.Create(pending.Hash, pending.Notice.ExpiresAt); }
        catch (ArgumentException) when (clock.GetUtcNow() >= pending.Notice.ExpiresAt) { return false; }
        return approvals.TryConsume(permission.Id, hash);
    }

    private Task NotifyAsync(ApprovalOutcome outcome, CancellationToken cancellationToken) => channel is null
        ? Task.CompletedTask : outcomes.Writer.WriteAsync(outcome, cancellationToken).AsTask();

    private async Task SendOutcomesAsync(CancellationToken cancellationToken)
    {
        if (channel is null)
            return;
        try
        {
            await foreach (var outcome in outcomes.Reader.ReadAllAsync(cancellationToken))
            {
                // A foglalt helyre várás nem küldési retry: a csatorna egyszer kapja meg a végállapotot.
                _ = await channelWork.SendAsync(token => channel.SendOutcomeAsync(outcome, token), cancellationToken, waitForSlot: true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task CompleteOriginalAsync(PendingOperation pending)
    {
        try
        {
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var scope = scopes.CreateScope();
            var state = pending.State switch
            {
                ApprovalState.Executed => "Succeeded",
                ApprovalState.Rejected => "Rejected",
                ApprovalState.Expired => "Expired",
                _ => "Failed"
            };
            await scope.ServiceProvider.GetRequiredService<IAuditStore>().CompletePendingAsync(
                pending.Notice.Id, state, state == "Failed" ? pending.Result : null, completion.Token);
        }
        catch (Exception)
        {
            lock (pending.Sync)
            {
                pending.State = ApprovalState.Failed;
                pending.Result = "Az eredeti audit lezárása sikertelen; tartós végállapot nem igazolható. Ez nem visszagörgetés.";
            }
            using var failure = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await RecordLifecycleAsync(pending, "failed", null, failure.Token);
        }
    }

    private async Task<bool> RecordLifecycleAsync(PendingOperation pending, string phase, ApprovalDecision? decision, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IAuditStore>();
            var id = Guid.NewGuid();
            var parameters = JsonSerializer.Serialize(new
            {
                requestId = pending.Notice.Id,
                phase,
                @event = phase switch
                {
                    "decision" => decision?.Choice == ApprovalChoice.Approve ? "approved" : "rejected",
                    "outcome" => pending.State == ApprovalState.Executed ? "executed" : "failed",
                    _ => phase
                },
                state = pending.State.ToString(),
                result = phase is "outcome" or "failed" ? pending.Result : null,
                actor = SecretRedactor.RedactText(decision?.Actor),
                channel = SecretRedactor.RedactText(channel?.Name),
                decidedAt = decision?.DecidedAt,
                choice = decision is null ? null : decision.Choice == ApprovalChoice.Approve ? "approve" : "reject"
            });
            await store.BeginAsync(new AuditCall(id, clock.GetUtcNow(), SecretRedactor.RedactText(pending.Owner)!,
                "approval_lifecycle", pending.Operation.Risk, parameters), cancellationToken);
            await store.CompleteAsync(id, "Succeeded", 0, null, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            lock (pending.Sync)
            {
                pending.State = ApprovalState.Failed;
                pending.Result = "A jóváhagyási audit sikertelen.";
            }
            // Ez új hibaesemény, nem a meghiúsult írás vagy a művelet újrapróbálása.
            if (phase != "failed")
            {
                using var failure = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await RecordLifecycleAsync(pending, "failed", null, failure.Token);
            }
            return false;
        }
    }

    public async Task<Guid?> RequestAsync(Guid id, string tool, RiskLevel risk, IReadOnlyList<string> allowlist,
        IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        if (!readerHealthy || channel?.IsAvailable != true)
            return null;
        var handler = handlers.SingleOrDefault(item => item.Tool == tool && item.Risk == risk);
        if (handler is null)
            return null;
        var operation = handler.Prepare(arguments);
        if (operation.Tool != tool || operation.Risk != risk)
            return null;
        var summary = JsonSerializer.Serialize(SecretRedactor.RedactParameters(operation.Parameters
            .Where(pair => allowlist.Contains(pair.Key, StringComparer.Ordinal))
            .ToDictionary(pair => pair.Key, pair => SecretRedactor.RedactText(pair.Value))));
        var owner = auditOptions.Value.Caller;
        var notice = new ApprovalNotice(id, SecretRedactor.RedactText(owner)!, tool, risk, summary,
            clock.GetUtcNow().AddMinutes(gateOptions.Value.LifetimeMinutes));
        var pending = new PendingOperation(operation, operation.ComputeHash(owner), owner, notice);
        requests[id] = pending;
        var started = 0;
        var invalidated = 0;
        var revocationQueued = 0;
        void RevokeNotice()
        {
            if (Interlocked.Exchange(ref revocationQueued, 1) != 0) return;
            if (!outcomes.Writer.TryWrite(new ApprovalOutcome(id, ApprovalState.Failed, "A kérés nem véglegesíthető; nincs végrehajtási engedély.")))
                readerHealthy = false;
        }
        try
        {
            if (!await channelWork.SendAsync(async token =>
            {
                Interlocked.Exchange(ref started, 1);
                try { await channel.SendRequestAsync(notice, token); }
                finally { if (Volatile.Read(ref invalidated) != 0) RevokeNotice(); }
            }, cancellationToken))
                throw new InvalidOperationException();
            if (!await RecordLifecycleAsync(pending, "request_sent", null, cancellationToken))
                throw new InvalidOperationException();
            return id;
        }
        catch (Exception)
        {
            requests.TryRemove(id, out _);
            pending.Ready.TrySetResult(false);
            if (pending.State != ApprovalState.Failed)
            {
                pending.State = ApprovalState.Failed;
                using var failure = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await RecordLifecycleAsync(pending, "failed", null, failure.Token);
            }
            Volatile.Write(ref invalidated, 1);
            if (Volatile.Read(ref started) != 0) RevokeNotice();
            return null;
        }
    }

    private sealed record PendingOperation(PreparedOperation Operation, string Hash, string Owner, ApprovalNotice Notice)
    {
        public object Sync { get; } = new();
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Claimed { get; set; }
        public ApprovalState State { get; set; } = ApprovalState.Pending;
        public string? Result { get; set; }
    }
}
