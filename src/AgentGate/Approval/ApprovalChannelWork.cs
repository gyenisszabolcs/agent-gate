namespace AgentGate.Approval;

/// <summary>Legfeljebb egy nyilvántartott csatornaküldés, korlátos várakozással és leállításkori felügyelettel.</summary>
internal sealed class ApprovalChannelWork(TimeProvider clock, TimeSpan timeout) : IDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource stopping = new();
    private Task<bool>? active;
    private bool stopped;
    private bool disposed;
    private int waitingForSlot;

    public async Task<bool> SendAsync(Func<CancellationToken, Task> send, CancellationToken cancellationToken, bool waitForSlot = false)
    {
        Task<bool> work;
        var waiting = false;
        try
        {
            while (true)
            {
                Task<bool>? occupied;
                lock (sync)
                {
                    if (stopped || cancellationToken.IsCancellationRequested) return false;
                    if (waitForSlot && !waiting) { waitingForSlot++; waiting = true; }
                    if (active is { IsCompleted: false })
                    {
                        if (!waitForSlot) return false;
                        occupied = active;
                    }
                    else
                    {
                        if (!waitForSlot && waitingForSlot != 0) return false;
                        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
                        cancellation.CancelAfter(timeout);
                        var token = cancellation.Token;
                        active = work = Task.Run(async () =>
                        {
                            try
                            {
                                await send(token);
                                return !token.IsCancellationRequested;
                            }
                            catch (Exception) { return false; }
                            finally { cancellation.Dispose(); }
                        }, CancellationToken.None);
                        break;
                    }
                }
                try { await occupied.WaitAsync(cancellationToken); }
                catch (Exception) { return false; }
            }
        }
        finally
        {
            if (waiting) lock (sync) waitingForSlot--;
        }
        try { return await work.WaitAsync(timeout, clock, cancellationToken); }
        catch (Exception) { return false; }
        // A timeout nem engedi el a foglalt helyet. Csak a tényleges befejezés után indulhat új küldés.
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task<bool>? work;
        Task cancellation;
        lock (sync)
        {
            if (disposed)
                return;
            stopped = true;
            work = active;
            cancellation = stopping.CancelAsync();
        }
        try { await cancellation.WaitAsync(cancellationToken); }
        catch (Exception) { /* A leállítási határidő és a nyers callbackhiba nem kerül diagnosztikába. */ }
        if (work is not null)
        {
            try { await work.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            stopped = true;
            try { stopping.Cancel(); }
            catch (Exception) { /* A csatorna hibás megszakítási callbackje nem lépheti át a titokhatárt. */ }
            stopping.Dispose();
        }
    }
}
