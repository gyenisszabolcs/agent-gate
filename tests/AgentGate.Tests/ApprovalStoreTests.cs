using System.Security.Cryptography;
using System.Text;
using AgentGate.Approval;
using Xunit;

namespace AgentGate.Tests;

public sealed class ApprovalStoreTests
{
    private readonly ManualTimeProvider clock = new();

    [Fact]
    public void CreatePreservesHashAndUtcExpiryWithUniqueIdentifiers()
    {
        var store = new InMemoryApprovalStore(clock);
        var hash = Hash("tesztművelet:lámpa:bekapcsolás");
        var expiresAt = clock.GetUtcNow().AddMinutes(1);

        var first = store.Create(hash, expiresAt);
        var second = store.Create(hash, expiresAt);

        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(hash, first.OperationHash);
        Assert.Equal(expiresAt, first.ExpiresAt);
        Assert.Equal(TimeSpan.Zero, first.ExpiresAt.Offset);
    }

    [Fact]
    public void MatchingApprovalCanBeConsumed()
    {
        var store = new InMemoryApprovalStore(clock);
        var hash = Hash("tesztművelet:lámpa:bekapcsolás");
        var request = store.Create(hash, clock.GetUtcNow().AddMinutes(1));

        Assert.True(store.TryConsume(request.Id, hash));
    }

    [Fact]
    public void MissingApprovalIsRejected()
    {
        var store = new InMemoryApprovalStore(clock);
        Assert.False(store.TryConsume(Guid.NewGuid(), Hash("tesztművelet")));
        Assert.False(store.TryConsume(Guid.Empty, Hash("tesztművelet")));
    }

    [Fact]
    public void ConsumedApprovalIsRejected()
    {
        var store = new InMemoryApprovalStore(clock);
        var hash = Hash("tesztművelet");
        var request = store.Create(hash, clock.GetUtcNow().AddMinutes(1));
        Assert.True(store.TryConsume(request.Id, hash));
        Assert.False(store.TryConsume(request.Id, hash));
    }

    [Fact]
    public async Task ConcurrentConsumersHaveExactlyOneWinner()
    {
        var store = new InMemoryApprovalStore(clock);
        var hash = Hash("tesztművelet");
        var request = store.Create(hash, clock.GetUtcNow().AddMinutes(1));
        using var start = new Barrier(2);
        bool Consume()
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)), "A két fogyasztó nem indult el.");
            return store.TryConsume(request.Id, hash);
        }

        var first = Task.Factory.StartNew(Consume, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var second = Task.Factory.StartNew(Consume, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, success => success);
        Assert.False(store.TryConsume(request.Id, hash));
    }

    [Fact]
    public void DifferentHashIsRejectedWithoutConsumingApproval()
    {
        var store = new InMemoryApprovalStore(clock);
        var hash = Hash("tesztművelet:lámpa:bekapcsolás");
        var request = store.Create(hash, clock.GetUtcNow().AddMinutes(1));

        Assert.False(store.TryConsume(request.Id, Hash("tesztművelet:lámpa:kikapcsolás")));
        Assert.True(store.TryConsume(request.Id, hash));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void ExpiryBoundaryUsesInjectedClock(long ticksFromExpiry, bool expected)
    {
        var store = new InMemoryApprovalStore(clock);
        var hash = Hash("tesztművelet");
        var expiry = clock.GetUtcNow().AddMinutes(1);
        var request = store.Create(hash, expiry);
        clock.UtcNow = expiry.AddTicks(ticksFromExpiry);

        Assert.Equal(expected, store.TryConsume(request.Id, hash));
    }

    public static IEnumerable<object?[]> InvalidHashes()
    {
        var hash = Hash("tesztművelet");
        yield return [null];
        yield return [string.Empty];
        yield return [" "];
        yield return [hash[..63]];
        yield return [hash + "0"];
        yield return [hash.ToUpperInvariant()];
        yield return [" " + hash];
        yield return [hash + "\n"];
        yield return ["g" + hash[1..]];
        yield return ["ａ" + hash[1..]];
    }

    [Theory]
    [MemberData(nameof(InvalidHashes))]
    public void CreateRejectsMalformedHash(string? malformedHash)
    {
        var store = new InMemoryApprovalStore(clock);
        var error = Assert.Throws<ArgumentException>(() => store.Create(malformedHash!, clock.GetUtcNow().AddMinutes(1)));
        Assert.Equal("operationHash", error.ParamName);
    }

    [Theory]
    [MemberData(nameof(InvalidHashes))]
    public void MalformedHashIsRejectedWithoutConsumingApproval(string? malformedHash)
    {
        var store = new InMemoryApprovalStore(clock);
        var hash = Hash("tesztművelet");
        var request = store.Create(hash, clock.GetUtcNow().AddMinutes(1));

        var error = Assert.Throws<ArgumentException>(() => store.TryConsume(request.Id, malformedHash!));
        Assert.Equal("operationHash", error.ParamName);
        Assert.True(store.TryConsume(request.Id, hash));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void CreateRejectsNonFutureExpiry(long ticksFromNow)
    {
        var store = new InMemoryApprovalStore(clock);
        var error = Assert.Throws<ArgumentException>(() => store.Create(Hash("tesztművelet"), clock.GetUtcNow().AddTicks(ticksFromNow)));
        Assert.Equal("expiresAt", error.ParamName);
    }

    [Fact]
    public void CreateRejectsNonUtcExpiryInsteadOfNormalizing()
    {
        var store = new InMemoryApprovalStore(clock);
        var expiry = clock.GetUtcNow().AddMinutes(1).ToOffset(TimeSpan.FromHours(1));
        var error = Assert.Throws<ArgumentException>(() => store.Create(Hash("tesztművelet"), expiry));
        Assert.Equal("expiresAt", error.ParamName);
    }

    [Fact]
    public void NullTimeProviderIsRejected()
    {
        var error = Assert.Throws<ArgumentNullException>(() => new InMemoryApprovalStore(null!));
        Assert.Equal("timeProvider", error.ParamName);
    }

    [Fact]
    public void ObservedExpiryCannotBeReversedByClockRollback()
    {
        var store = new InMemoryApprovalStore(clock);
        var hash = Hash("tesztművelet");
        var initialTime = clock.GetUtcNow();
        var expiry = initialTime.AddMinutes(1);
        var request = store.Create(hash, expiry);
        clock.UtcNow = expiry;
        Assert.False(store.TryConsume(request.Id, hash));

        clock.UtcNow = initialTime;
        Assert.False(store.TryConsume(request.Id, hash));
    }

    [Fact]
    public void NewStoreDoesNotRecoverAnotherInstancesApproval()
    {
        var original = new InMemoryApprovalStore(clock);
        var replacement = new InMemoryApprovalStore(clock);
        var hash = Hash("tesztművelet");
        var request = original.Create(hash, clock.GetUtcNow().AddMinutes(1));

        Assert.False(replacement.TryConsume(request.Id, hash));
        Assert.True(original.TryConsume(request.Id, hash));
    }

    private static string Hash(string operation) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(operation)));

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
