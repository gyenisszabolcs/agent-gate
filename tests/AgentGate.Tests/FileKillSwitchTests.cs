using System.Text;
using AgentGate.Policy;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class FileKillSwitchTests
{
    [Fact]
    public async Task PermissionFailureAfterFalseClosesWithoutCachingAndRecovers()
    {
        var reads = 0;
        using var cancellation = new CancellationTokenSource();
        var source = new FileKillSwitchStateSource(Options.Create(new KillSwitchFileOptions()), (_, token) =>
        {
            Assert.True(token == cancellation.Token, "Az I/O megszakítási tokenje eltér.");
            reads++;
            return reads == 2 ? Task.FromException<byte[]>(new UnauthorizedAccessException("Tesztjogosultság."))
                : Task.FromResult(Encoding.UTF8.GetBytes("{\"Enabled\":false}"));
        });
        Assert.False(await source.IsEnabledAsync(cancellation.Token));
        Assert.True(await source.IsEnabledAsync(cancellation.Token));
        Assert.False(await source.IsEnabledAsync(cancellation.Token));
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task CancellationDuringFileReadIsNotConvertedIntoAnAllowDecision()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FileKillSwitchStateSource(Options.Create(new KillSwitchFileOptions()), async (_, token) =>
        {
            Assert.True(token == cancellation.Token, "Az I/O megszakítási tokenje eltér.");
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return [];
        });
        var pending = source.IsEnabledAsync(cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Utf8BomDoesNotTurnExplicitFalseIntoClosedState()
    {
        var directory = Directory.CreateTempSubdirectory("agent-gate-switch-").FullName;
        try
        {
            var path = Path.Combine(directory, "switch.json");
            await File.WriteAllTextAsync(path, "{\"Enabled\":false}", new UTF8Encoding(true));
            var source = new FileKillSwitchStateSource(Options.Create(new KillSwitchFileOptions { Path = path }));
            Assert.False(await source.IsEnabledAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task NonUtf8JsonCannotOpenSwitch()
    {
        var directory = Directory.CreateTempSubdirectory("agent-gate-switch-").FullName;
        try
        {
            var path = Path.Combine(directory, "switch.json");
            await File.WriteAllTextAsync(path, "{\"Enabled\":false}", Encoding.Unicode);
            var source = new FileKillSwitchStateSource(Options.Create(new KillSwitchFileOptions { Path = path }));
            Assert.True(await source.IsEnabledAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
