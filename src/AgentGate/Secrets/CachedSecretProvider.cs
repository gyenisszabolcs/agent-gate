namespace AgentGate.Secrets;

// Folyamaton belüli cache; a TTL kiadhatósági határ, nem fizikai RAM-törlési garancia.
public sealed class CachedSecretProvider(ISecretProvider source, TimeProvider clock) : ISecretProvider, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly object sync = new();
    private readonly Dictionary<Guid, Slot> slots = [];
    private readonly HashSet<Task> workers = [];
    private readonly CancellationTokenSource stopping = new();
    private bool stopped;
    private bool disposed;

    private sealed class Slot
    {
        public SecretLease? Lease;
        public long CompletedAt;
        public long Generation;
        public Task<SecretLease?>? Flight;
    }

    public async Task<string> GetSecretAsync(Guid secretId, CancellationToken cancellationToken = default) =>
        (await GetSecretLeaseAsync(secretId, cancellationToken).ConfigureAwait(false)).Value;

    public async Task<SecretLease> GetSecretLeaseAsync(Guid secretId, CancellationToken cancellationToken = default)
    {
        Validate(secretId, cancellationToken);
        while (true)
        {
            Task<SecretLease?> flight;
            lock (sync)
            {
                ThrowIfStopped();
                ThrowIfCanceled(cancellationToken);
                if (!slots.TryGetValue(secretId, out var slot)) slots.Add(secretId, slot = new Slot());
                if (slot.Lease is not null && clock.GetElapsedTime(slot.CompletedAt) < Lifetime) return slot.Lease;
                // A lejárt értéket nem használjuk hibás frissítés tartalékaként sem.
                slot.Lease = null;
                flight = slot.Flight ?? StartFetch(secretId, slot);
            }
            var lease = await WaitForAsync(flight, cancellationToken).ConfigureAwait(false);
            lock (sync)
            {
                ThrowIfStopped();
                ThrowIfCanceled(cancellationToken);
                if (lease is not null && slots.TryGetValue(secretId, out var slot) &&
                    ReferenceEquals(slot.Lease, lease) && clock.GetElapsedTime(slot.CompletedAt) < Lifetime) return lease;
            }
            // Visszavont, közben lejárt eredményt a már várakozó hívó sem kaphat meg.
        }
    }

    public async Task<string> GetFreshSecretAsync(Guid secretId, CancellationToken cancellationToken = default)
    {
        Validate(secretId, cancellationToken);
        Task<SecretLease?> flight;
        lock (sync)
        {
            ThrowIfStopped();
            // Az önteszt nem olvas és nem tölt cache-t, de munkáját ugyanaz az életciklus birtokolja.
            flight = StartFetch(secretId, null);
        }
        var lease = await WaitForAsync(flight, cancellationToken).ConfigureAwait(false);
        lock (sync)
        {
            ThrowIfStopped();
            ThrowIfCanceled(cancellationToken);
            return lease!.Value;
        }
    }

    public void Invalidate(Guid secretId, SecretLease rejectedLease)
    {
        lock (sync)
        {
            if (slots.TryGetValue(secretId, out var slot) && ReferenceEquals(slot.Lease, rejectedLease))
                slot.Lease = null;
            // Késői régi 401 nem vonhatja vissza az új értéket vagy a már futó frissítést.
        }
    }

    // Belső szerveroldali visszavonás: a még futó generáció eredménye sem tölthet vissza.
    public void Invalidate(Guid secretId)
    {
        lock (sync)
        {
            if (!slots.TryGetValue(secretId, out var slot)) return;
            slot.Lease = null;
            slot.Generation++;
        }
    }

    private Task<SecretLease?> StartFetch(Guid id, Slot? slot)
    {
        var completion = new TaskCompletionSource<SecretLease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = slot?.Generation;
        if (slot is not null) slot.Flight = completion.Task;
        // A szinkron natív belépés nem blokkolhatja a lockot vagy a hívó megszakítását.
        // A worker nem hibásodik meg: minden hibát biztonságosan közvetít és megfigyel.
        var worker = Task.Run(async () =>
        {
            try
            {
                var value = await source.GetSecretAsync(id, stopping.Token).ConfigureAwait(false);
                lock (sync)
                {
                    if (stopped) throw new OperationCanceledException();
                    if (string.IsNullOrEmpty(value)) throw new SecretProviderException(SecretProviderError.NotFound);
                    var lease = new SecretLease(value);
                    if (slot is not null)
                    {
                        slot.Flight = null;
                        if (slot.Generation != generation) { completion.SetResult(null); return; }
                        slot.Lease = lease;
                        slot.CompletedAt = clock.GetTimestamp();
                    }
                    completion.SetResult(lease);
                }
            }
            catch (Exception error)
            {
                lock (sync)
                {
                    if (slot is not null) slot.Flight = null;
                    if (stopped) completion.TrySetCanceled(stopping.Token);
                    else completion.TrySetException(new SecretProviderException(
                        error is SecretProviderException safe ? safe.Error : SecretProviderError.ProviderFailure));
                    // Ha már minden várakozó megszakadt, akkor is megfigyeljük a késői hibát.
                    _ = completion.Task.Exception;
                }
            }
        });
        workers.Add(worker);
        _ = worker.ContinueWith(completed => { lock (sync) workers.Remove(completed); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion.Task;
    }

    private static void Validate(Guid id, CancellationToken ct)
    {
        ThrowIfCanceled(ct);
        if (id == Guid.Empty) throw new ArgumentException("A titokazonosító nem lehet üres.", nameof(id));
    }

    private void ThrowIfStopped()
    {
        if (stopped) throw new OperationCanceledException("A titokszolgáltató leállt.");
    }

    private static void ThrowIfCanceled(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) throw new OperationCanceledException("A titok lekérése megszakítva.", ct);
    }

    private static async Task<SecretLease?> WaitForAsync(Task<SecretLease?> flight, CancellationToken ct)
    {
        try { return await flight.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("A titok lekérése megszakítva.", ct);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] pending;
        lock (sync)
        {
            stopped = true;
            slots.Clear();
            pending = workers.ToArray();
        }
        try { await stopping.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { /* Helyettesített provider visszahívása nem juthat ki. */ }
        // A host határideje lejárhat, a natív munka tulajdonosa akkor is a cache marad.
        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        // A natív SDK nem megszakítható: felszabadításkor a tényleges befejezést várjuk.
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            stopping.Dispose();
        }
    }
}
