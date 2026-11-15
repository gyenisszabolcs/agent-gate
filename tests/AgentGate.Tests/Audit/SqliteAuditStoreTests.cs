using AgentGate.Audit;
using AgentGate.Policy;
using Xunit;

namespace AgentGate.Tests.Audit;

public sealed class SqliteAuditStoreTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("agent-gate-audit-").FullName;
    private string DatabasePath => Path.Combine(directory, "audit.db");

    private static SanitizedAuditEntry CreateEntry() => new(
        new DateTimeOffset(2026, 11, 15, 1, 2, 3, TimeSpan.Zero).AddTicks(1234),
        "test-caller", "test-tool", "{\"value\":\"[REDACTED]\"}", RiskLevel.Read, "rendben");

    [Fact]
    public async Task AppendAndReadPreservesEveryField()
    {
        var store = new SqliteAuditStore(DatabasePath);
        var entry = CreateEntry();

        await store.AppendAsync(entry, CancellationToken.None);
        var entries = await store.ReadAllAsync(CancellationToken.None);

        Assert.Equal(entry, Assert.Single(entries));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative.db")]
    [InlineData(":memory:")]
    [InlineData("file:audit.db?mode=memory")]
    [InlineData("bad\0path")]
    public void InvalidPathIsRejectedWithoutCreatingFiles(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SqliteAuditStore(path!));
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public void DirectoryPathIsRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SqliteAuditStore(directory));
    }

    [Fact]
    public void MissingParentIsRejectedWithoutCreatingIt()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SqliteAuditStore(Path.Combine(directory, "missing", "audit.db")));
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public async Task InvalidEntryIsRejectedBeforeDatabaseCreation()
    {
        var store = new SqliteAuditStore(DatabasePath);
        SanitizedAuditEntry[] invalidEntries =
        [
            CreateEntry() with { TimestampUtc = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(1)) },
            CreateEntry() with { Caller = " " },
            CreateEntry() with { Tool = "" },
            CreateEntry() with { SanitizedParameters = null! },
            CreateEntry() with { SanitizedResult = null! },
            CreateEntry() with { RiskLevel = (RiskLevel)999 }
        ];
        foreach (var entry in invalidEntries)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.AppendAsync(entry, CancellationToken.None));
        }

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.AppendAsync(null!, CancellationToken.None));
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task CancelledOperationsDoNotCreateDatabase()
    {
        var store = new SqliteAuditStore(DatabasePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AppendAsync(CreateEntry(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAllAsync(cancellation.Token));
        Assert.False(File.Exists(DatabasePath));
    }

    [Fact]
    public async Task ReopeningDatabasePreservesEntriesAndInsertionOrder()
    {
        var first = CreateEntry();
        var second = first with { Caller = "second-caller", RiskLevel = RiskLevel.Forbidden };
        await new SqliteAuditStore(DatabasePath).AppendAsync(first, CancellationToken.None);
        Assert.True(File.Exists(DatabasePath));
        await new SqliteAuditStore(DatabasePath).AppendAsync(second, CancellationToken.None);

        var entries = await new SqliteAuditStore(DatabasePath).ReadAllAsync(CancellationToken.None);

        Assert.Equal(new[] { first, second }, entries);
    }

    [Fact]
    public async Task SqlCharactersRemainLiteralInEveryTextField()
    {
        const string value = "árvíztűrő '); DROP TABLE AuditEntries; -- \" ; $result\n\\";
        var entry = CreateEntry() with
        {
            Caller = value,
            Tool = value,
            SanitizedParameters = value,
            SanitizedResult = value
        };
        var path = Path.Combine(directory, "audit;Mode=Memory;quotes'.db");
        var store = new SqliteAuditStore(path);

        await store.AppendAsync(entry, CancellationToken.None);
        await store.AppendAsync(CreateEntry(), CancellationToken.None);

        Assert.Equal(new[] { entry, CreateEntry() }, await new SqliteAuditStore(path).ReadAllAsync(CancellationToken.None));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task NewDatabaseReadsAsEmpty()
    {
        Assert.Empty(await new SqliteAuditStore(DatabasePath).ReadAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SeparateDatabasesDoNotShareEntries()
    {
        await new SqliteAuditStore(DatabasePath).AppendAsync(CreateEntry(), CancellationToken.None);
        var other = new SqliteAuditStore(Path.Combine(directory, "other.db"));

        Assert.Empty(await other.ReadAllAsync(CancellationToken.None));
        Assert.Single(await new SqliteAuditStore(DatabasePath).ReadAllAsync(CancellationToken.None));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
