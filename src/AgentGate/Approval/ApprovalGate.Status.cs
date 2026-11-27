using AgentGate.Policy;
using AgentGate.Tools;

namespace AgentGate.Approval;

public sealed partial class ApprovalGate
{
    // Külön korlát: a státuszvárás nem foglal jóváhagyási/küldési munkáshelyet.
    internal const int MaximumStatusWaits = 64;
    private readonly SemaphoreSlim statusSlots = new(MaximumStatusWaits, MaximumStatusWaits);
    private readonly CancellationTokenSource statusStopping = new();
    private int statusDisposed;
    private readonly object statusLifetime = new();
    internal int ActiveStatusWaits => MaximumStatusWaits - statusSlots.CurrentCount;

    private void StopStatusWaits(bool dispose)
    {
        lock (statusLifetime)
        {
            if (statusDisposed != 0) return;
            statusStopping.Cancel();
            if (dispose)
            {
                statusDisposed = 1;
                statusStopping.Dispose();
            }
        }
    }

    private CancellationToken StatusStopToken()
    {
        lock (statusLifetime) return statusDisposed != 0 ? new CancellationToken(canceled: true) : statusStopping.Token;
    }

    public async Task<ApprovalOutcome?> GetStatusAsync(Guid id, int waitSeconds = 0, CancellationToken cancellationToken = default)
    {
        if (waitSeconds is < 0 or > 120) throw new GateToolException("A wait_seconds csak 0 és 120 közötti egész szám lehet.");
        cancellationToken.ThrowIfCancellationRequested();
        var current = GetStatus(id);
        if (waitSeconds == 0 || current is null || current.State is not (ApprovalState.Pending or ApprovalState.Approved)) return current;
        if (!statusSlots.Wait(0)) throw new GateToolException("Túl sok párhuzamos státuszvárás; később kérdezze le újra az állapotot.");
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(waitSeconds), clock);
            var hostStop = StatusStopToken();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hostStop, budget.Token);
            using var scope = scopes.CreateScope();
            var killSwitch = scope.ServiceProvider.GetRequiredService<IKillSwitchStateSource>();
            try
            {
                while (true)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (!requests.TryGetValue(id, out var pending) ||
                        !string.Equals(pending.Owner, auditOptions.Value.Caller, StringComparison.Ordinal)) return null;
                    Task changed;
                    ApprovalState observed;
                    // A pillanatkép és a feliratkozás egy lock alatt készül: nincs elvesző döntés.
                    lock (pending.Sync)
                    {
                        if (pending.Status.State != current.State ||
                            pending.Status.State is not (ApprovalState.Pending or ApprovalState.Approved))
                            return pending.Status;
                        observed = pending.Status.State;
                        changed = pending.Changed.Task;
                    }
                    // Nincs fájlwatcher vagy háttérfeladat; a rövid ellenőrzés a kill switch hibájára is zár.
                    if (await killSwitch.IsEnabledAsync(stop.Token)) return GetStatus(id);
                    try { await changed.WaitAsync(TimeSpan.FromMilliseconds(100), clock, stop.Token); }
                    catch (TimeoutException) { }
                    var latest = GetStatus(id);
                    if (latest is null || latest.State != observed) return latest;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                !hostStop.IsCancellationRequested && budget.IsCancellationRequested)
            {
                return GetStatus(id);
            }
        }
        finally { statusSlots.Release(); }
    }
}
