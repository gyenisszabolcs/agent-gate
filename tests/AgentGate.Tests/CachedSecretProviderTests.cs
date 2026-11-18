using AgentGate.Secrets;
using Xunit;

namespace AgentGate.Tests;

public sealed class CachedSecretProviderTests
{
    [Fact]
    public async Task HitAndDistinctKeysUseOneFetchPerKey()
    {
        var raw = new Source();
        await using var cache = new CachedSecretProvider(raw, new Clock());
        var id = Guid.NewGuid();
        var first = await cache.GetSecretLeaseAsync(id);
        Assert.Same(first, await cache.GetSecretLeaseAsync(id));
        Assert.NotSame(first, await cache.GetSecretLeaseAsync(Guid.NewGuid()));
        Assert.Equal(2, raw.Calls);
    }

    [Fact]
    public async Task FixedTtlExpiresAtExactBoundaryWithoutSliding()
    {
        var clock = new Clock();
        var raw = new Source();
        await using var cache = new CachedSecretProvider(raw, clock);
        var id = Guid.NewGuid();
        var first = await cache.GetSecretLeaseAsync(id);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Same(first, await cache.GetSecretLeaseAsync(id));
        clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1));
        Assert.Same(first, await cache.GetSecretLeaseAsync(id));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.NotSame(first, await cache.GetSecretLeaseAsync(id));
        Assert.Equal(2, raw.Calls);
    }

    [Fact]
    public async Task TtlStartsAtSuccessfulCompletionNotRequestStart()
    {
        var clock = new Clock();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = Signal();
        var raw = new Source { Read = (_, _) => { entered.SetResult(); return release.Task; } };
        await using var cache = new CachedSecretProvider(raw, clock);
        var id = Guid.NewGuid();
        var pending = cache.GetSecretLeaseAsync(id);
        await entered.Task;
        clock.Advance(TimeSpan.FromMinutes(10));
        release.SetResult(NewValue());
        var lease = await pending;
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Same(lease, await cache.GetSecretLeaseAsync(id));
        Assert.Equal(1, raw.Calls);
    }

    [Fact]
    public async Task ParallelWaitersShareOneFlightWhileOtherKeysProceed()
    {
        var entered = Signal();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = new Source { Read = (call, _) => { if (call != 1) return Task.FromResult(NewValue()); entered.SetResult(); return release.Task; } };
        await using var cache = new CachedSecretProvider(raw, new Clock());
        var id = Guid.NewGuid();
        var first = cache.GetSecretLeaseAsync(id);
        await entered.Task;
        var waiters = Enumerable.Range(0, 16).Select(_ => cache.GetSecretLeaseAsync(id)).ToArray();
        var other = await cache.GetSecretLeaseAsync(Guid.NewGuid()).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, raw.Calls);
        release.SetResult(NewValue());
        var lease = await first;
        foreach (var result in await Task.WhenAll(waiters)) Assert.Same(lease, result);
        Assert.NotSame(other, lease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureIsSanitizedAndNeverCachesOrFallsBack(bool expired)
    {
        var hidden = NewValue();
        var raw = new Source();
        var clock = new Clock();
        await using var cache = new CachedSecretProvider(raw, clock);
        var id = Guid.NewGuid();
        if (expired) { await cache.GetSecretAsync(id); clock.Advance(TimeSpan.FromMinutes(5)); }
        raw.Read = (_, _) => throw new InvalidOperationException(hidden, new Exception(hidden));
        var error = await Record.ExceptionAsync(() => cache.GetSecretAsync(id));
        Assert.True(error is SecretProviderException && error.InnerException is null && error.Data.Count == 0 &&
            !error.ToString().Contains(hidden, StringComparison.Ordinal), "Nem biztonságos cache-hiba.");
        raw.Read = (_, _) => Task.FromResult(NewValue());
        await cache.GetSecretAsync(id);
        Assert.Equal(expired ? 3 : 2, raw.Calls);
    }

    [Fact]
    public async Task EmptyResultIsNotCached()
    {
        var raw = new Source { Read = (_, _) => Task.FromResult("") };
        await using var cache = new CachedSecretProvider(raw, new Clock());
        var id = Guid.NewGuid();
        var error = await Record.ExceptionAsync(() => cache.GetSecretAsync(id));
        Assert.True(error is SecretProviderException);
        raw.Read = (_, _) => Task.FromResult(NewValue());
        await cache.GetSecretAsync(id);
        Assert.Equal(2, raw.Calls);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelSharedFetchOrOtherWaiter()
    {
        var entered = Signal();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken owner = default;
        var raw = new Source { Read = (_, ct) => { owner = ct; entered.SetResult(); return release.Task; } };
        await using var cache = new CachedSecretProvider(raw, new Clock());
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var cancelled = cache.GetSecretAsync(id, cancellation.Token);
        await entered.Task;
        var survivor = cache.GetSecretLeaseAsync(id);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(owner.IsCancellationRequested);
        release.SetResult(NewValue());
        var lease = await survivor;
        Assert.Same(lease, await cache.GetSecretLeaseAsync(id));
        Assert.Equal(1, raw.Calls);
    }

    [Fact]
    public async Task AllWaitersCanLeaveAndLaterFailureIsOwnedAndFreshCallRecovers()
    {
        var entered = Signal();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = new Source { Read = (_, _) => { entered.TrySetResult(); return release.Task; } };
        await using var cache = new CachedSecretProvider(raw, new Clock());
        using var ct = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var pending = cache.GetSecretAsync(id, ct.Token);
        await entered.Task;
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var joined = cache.GetSecretAsync(id);
        release.SetException(new Exception(NewValue()));
        var error = await Record.ExceptionAsync(() => joined);
        Assert.True(error is SecretProviderException);
        raw.Read = (_, _) => Task.FromResult(NewValue());
        await cache.GetSecretAsync(id);
        Assert.Equal(2, raw.Calls);
    }

    [Fact]
    public async Task InvalidateDuringFlightDiscardsResultWithoutParallelDuplicate()
    {
        var entered = Signal();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = NewValue();
        var fresh = NewValue();
        var raw = new Source { Read = (call, _) => { if (call != 1) return Task.FromResult(fresh); entered.SetResult(); return release.Task; } };
        await using var cache = new CachedSecretProvider(raw, new Clock());
        var id = Guid.NewGuid();
        var pending = cache.GetSecretAsync(id);
        await entered.Task;
        cache.Invalidate(id);
        var joined = cache.GetSecretAsync(id);
        Assert.Equal(1, raw.Calls);
        release.SetResult(old);
        Assert.True(await pending == fresh && await joined == fresh, "Visszavont érték jutott ki.");
        Assert.Equal(2, raw.Calls);
    }

    [Fact]
    public async Task ConditionalInvalidationUsesIdentityEvenWhenRotatedValueIsEqual()
    {
        var value = NewValue();
        var raw = new Source { Read = (_, _) => Task.FromResult(value) };
        await using var cache = new CachedSecretProvider(raw, new Clock());
        var id = Guid.NewGuid();
        var old = await cache.GetSecretLeaseAsync(id);
        cache.Invalidate(id, old);
        var fresh = await cache.GetSecretLeaseAsync(id);
        cache.Invalidate(id, old);
        cache.Invalidate(Guid.NewGuid(), fresh);
        Assert.Same(fresh, await cache.GetSecretLeaseAsync(id));
        Assert.Equal(2, raw.Calls);
        Assert.True(!fresh.ToString().Contains(value, StringComparison.Ordinal), "A referencia titkot ír ki.");
    }

    [Fact]
    public async Task FreshReadsBypassWarmCacheWithoutReplacingIt()
    {
        var raw = new Source();
        await using var cache = new CachedSecretProvider(raw, new Clock());
        var id = Guid.NewGuid();
        var warm = await cache.GetSecretLeaseAsync(id);
        await cache.GetFreshSecretAsync(id);
        await cache.GetFreshSecretAsync(id);
        Assert.Same(warm, await cache.GetSecretLeaseAsync(id));
        Assert.Equal(3, raw.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownOwnsUncancellableNativeWorkUntilItActuallyCompletes(bool fresh)
    {
        var entered = Signal();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken owner = default;
        var raw = new Source { Read = (_, ct) => { owner = ct; entered.SetResult(); return release.Task; } };
        var cache = new CachedSecretProvider(raw, new Clock());
        var id = Guid.NewGuid();
        var pending = fresh ? cache.GetFreshSecretAsync(id) : cache.GetSecretAsync(id);
        await entered.Task;
        using var deadline = new CancellationTokenSource();
        var stop = cache.StopAsync(deadline.Token);
        try
        {
            Assert.True(owner.IsCancellationRequested);
            Assert.False(stop.IsCompleted);
            deadline.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
            Assert.False(pending.IsCompleted);
            var rejected = await Record.ExceptionAsync(() => cache.GetSecretAsync(id));
            Assert.True(rejected is OperationCanceledException);
        }
        finally
        {
            release.SetResult(NewValue());
            await cache.DisposeAsync();
        }
        var error = await Record.ExceptionAsync(() => pending);
        Assert.True(error is OperationCanceledException);
        Assert.Equal(1, raw.Calls);
    }

    [Fact]
    public async Task SynchronousNativeEntryCannotBlockWaiterCancellationOrStartDuplicateFetch()
    {
        var entered = Signal();
        var release = Signal();
        var raw = new Source { Read = (_, _) => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); return Task.FromResult(NewValue()); } };
        var cache = new CachedSecretProvider(raw, new Clock());
        using var ct = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var pending = cache.GetSecretAsync(id, ct.Token);
        Task<string>? joined = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            ct.Cancel();
            var error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(error is OperationCanceledException && error.Message == "A titok lekérése megszakítva.");
            joined = cache.GetSecretAsync(id);
            Assert.False(joined.IsCompleted);
            Assert.Equal(1, raw.Calls);
        }
        finally
        {
            release.TrySetResult();
            if (joined is not null) await joined;
            await cache.DisposeAsync();
        }
    }

    [Fact]
    public async Task AlreadyCancelledOrInvalidIdNeverStartsWork()
    {
        var raw = new Source();
        await using var cache = new CachedSecretProvider(raw, new Clock());
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetSecretAsync(Guid.NewGuid(), ct.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetSecretAsync(Guid.Empty));
        Assert.Equal(0, raw.Calls);
    }

    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    internal sealed class Source : ISecretProvider
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public Func<int, CancellationToken, Task<string>> Read { get; set; } = (_, _) => Task.FromResult(NewValue());
        public Task<string> GetSecretAsync(Guid secretId, CancellationToken cancellationToken = default) =>
            Read(Interlocked.Increment(ref calls), cancellationToken);
    }

    internal static string NewValue() => Convert.ToHexString(Guid.NewGuid().ToByteArray());
}
