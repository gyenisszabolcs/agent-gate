using AgentGate.Audit;
using Microsoft.Data.Sqlite;
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

    [Fact]
    public async Task InvalidBeginIsRejectedBeforeCreatingDatabase()
    {
        var store = new SqliteAuditStore(DatabasePath);
        var valid = new AuditCall(Guid.NewGuid(), DateTimeOffset.UtcNow, "caller", "tool", RiskLevel.Read, "{}");
        AuditCall[] invalid =
        [
            valid with { CallId = Guid.Empty },
            valid with { TimestampUtc = valid.TimestampUtc.ToOffset(TimeSpan.FromHours(1)) },
            valid with { Caller = " " },
            valid with { Tool = " " },
            valid with { RiskLevel = (RiskLevel)999 },
            valid with { SanitizedParameters = null! },
            valid with { State = "Succeeded" },
            valid with { DurationMilliseconds = 1 },
            valid with { SanitizedError = "hiba" }
        ];
        foreach (var call in invalid)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.BeginAsync(call, CancellationToken.None));
        }
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.BeginAsync(null!, CancellationToken.None));
        Assert.False(File.Exists(DatabasePath));
    }

    [Theory]
    [InlineData("Started", 1d)]
    [InlineData("unknown", 1d)]
    [InlineData("Succeeded", -1d)]
    [InlineData("Failed", double.NaN)]
    [InlineData("Cancelled", double.PositiveInfinity)]
    public async Task InvalidCompletionCannotOverwriteStartedCall(string state, double duration)
    {
        var store = new SqliteAuditStore(DatabasePath);
        var call = new AuditCall(Guid.NewGuid(), DateTimeOffset.UtcNow, "caller", "tool", RiskLevel.Read, "{}");
        await store.BeginAsync(call, CancellationToken.None);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.CompleteAsync(call.CallId, state, duration, null, CancellationToken.None));
    }

    [Fact]
    public async Task OldSchemaAndEntriesRemainReadableAfterNewCallLifecycle()
    {
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE AuditEntries (
                    Id INTEGER PRIMARY KEY, TimestampUtc TEXT NOT NULL, Caller TEXT NOT NULL,
                    Tool TEXT NOT NULL, SanitizedParameters TEXT NOT NULL, RiskLevel INTEGER NOT NULL,
                    SanitizedResult TEXT NOT NULL);
                INSERT INTO AuditEntries VALUES (1, '2026-11-15T01:02:03.0001234+00:00', 'test-caller',
                    'test-tool', '{"value":"[REDACTED]"}', 0, 'rendben');
                """;
            await command.ExecuteNonQueryAsync();
        }
        var store = new SqliteAuditStore(DatabasePath);
        Assert.Equal(CreateEntry(), Assert.Single(await store.ReadAllAsync(CancellationToken.None)));
        var call = new AuditCall(Guid.NewGuid(), DateTimeOffset.UtcNow, "caller", "tool", RiskLevel.Control, "{}");
        await store.BeginAsync(call, CancellationToken.None);
        await new SqliteAuditStore(DatabasePath).CompleteAsync(call.CallId, "Succeeded", 123.25, null, CancellationToken.None);
        Assert.Equal(CreateEntry(), Assert.Single(await new SqliteAuditStore(DatabasePath).ReadAllAsync(CancellationToken.None)));
        await using var reopened = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await reopened.OpenAsync();
        await using var read = reopened.CreateCommand();
        read.CommandText = "SELECT CallId, State, DurationMilliseconds, Caller, Tool, RiskLevel, TimestampUtc, SanitizedParameters, SanitizedError FROM AuditCalls";
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(call.CallId.ToString(), reader.GetString(0));
        Assert.Equal("Succeeded", reader.GetString(1));
        Assert.Equal(123.25, reader.GetDouble(2));
        Assert.Equal(call.Caller, reader.GetString(3));
        Assert.Equal(call.Tool, reader.GetString(4));
        Assert.Equal((int)call.RiskLevel, reader.GetInt32(5));
        Assert.Equal(call.TimestampUtc, DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("{}", reader.GetString(7));
        Assert.True(reader.IsDBNull(8));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task DuplicateBeginAndRepeatedOrMissingCompletionCannotOverwriteHistory()
    {
        var store = new SqliteAuditStore(DatabasePath);
        var call = new AuditCall(Guid.NewGuid(), DateTimeOffset.UtcNow, "caller", "tool", RiskLevel.Read, "{}");
        await store.BeginAsync(call, CancellationToken.None);
        await Assert.ThrowsAsync<SqliteException>(() => store.BeginAsync(call, CancellationToken.None));
        await store.CompleteAsync(call.CallId, "Failed", 10, "biztonságos hiba", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(call.CallId, "Succeeded", 20, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(Guid.NewGuid(), "Succeeded", 20, null, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledBeginAndCompletionDoNotCreateOrCompleteCall()
    {
        var store = new SqliteAuditStore(DatabasePath);
        var call = new AuditCall(Guid.NewGuid(), DateTimeOffset.UtcNow, "caller", "tool", RiskLevel.Read, "{}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.BeginAsync(call, cancellation.Token));
        Assert.False(File.Exists(DatabasePath));
        await store.BeginAsync(call, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompleteAsync(call.CallId, "Succeeded", 1, null, cancellation.Token));
        await store.CompleteAsync(call.CallId, "Cancelled", 1, "megszakadt", CancellationToken.None);
    }

    [Theory]
    [InlineData("Rejected")]
    [InlineData("Expired")]
    [InlineData("Failed")]
    [InlineData("Succeeded")]
    public async Task PendingCompletionIsConditionalAndPreservesOriginalIdentityAndDuration(string terminal)
    {
        var store = new SqliteAuditStore(DatabasePath);
        var call = new AuditCall(Guid.NewGuid(), DateTimeOffset.UtcNow, "caller", "tool", RiskLevel.System, "{}");
        await store.BeginAsync(call, CancellationToken.None);
        Task Complete(Guid id, string state) => store.CompletePendingAsync(id, state, null, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(call.CallId, terminal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Complete(Guid.NewGuid(), terminal));
        await store.CompleteAsync(call.CallId, "Pending", 12.5, null, CancellationToken.None);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Complete(call.CallId, "Cancelled"));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try { await Complete(call.CallId, terminal); return true; }
            catch (InvalidOperationException) { return false; }
        })));
        Assert.Equal(1, attempts.Count(value => value));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteAsync(call.CallId, "Succeeded", 0, null, CancellationToken.None));
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State, Caller, Tool, SanitizedParameters, DurationMilliseconds FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(terminal, reader.GetString(0));
        Assert.Equal(call.Caller, reader.GetString(1));
        Assert.Equal(call.Tool, reader.GetString(2));
        Assert.Equal(call.SanitizedParameters, reader.GetString(3));
        Assert.Equal(12.5, reader.GetDouble(4));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task PendingCompletionLeavesHistoricalRowsAndCancelledOrInvalidTargetsUntouched()
    {
        var store = new SqliteAuditStore(DatabasePath);
        var old = new AuditCall(Guid.NewGuid(), new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), "régi hívó", "régi eszköz", RiskLevel.System, "{\"text\":\"régi\"}");
        await store.BeginAsync(old, CancellationToken.None);
        await store.CompleteAsync(old.CallId, "Pending", 42, null, CancellationToken.None);
        var current = old with { CallId = Guid.NewGuid(), TimestampUtc = DateTimeOffset.UtcNow };
        await new SqliteAuditStore(DatabasePath).BeginAsync(current, CancellationToken.None);
        await store.CompleteAsync(current.CallId, "Pending", 1, null, CancellationToken.None);
        foreach (var state in new[] { "Started", "Pending", "Cancelled", "Executed", "unknown", "" })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.CompletePendingAsync(current.CallId, state, null, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.CompletePendingAsync(Guid.Empty, "Failed", null, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompletePendingAsync(current.CallId, "Failed", null, cancellation.Token));
        await new SqliteAuditStore(DatabasePath).CompletePendingAsync(current.CallId, "Rejected", null, CancellationToken.None);
        await using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State, DurationMilliseconds, SanitizedParameters FROM AuditCalls WHERE CallId = $id";
        command.Parameters.AddWithValue("$id", old.CallId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Pending", reader.GetString(0));
        Assert.Equal(42, reader.GetDouble(1));
        Assert.Equal(old.SanitizedParameters, reader.GetString(2));
        Assert.False(await reader.ReadAsync());
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
