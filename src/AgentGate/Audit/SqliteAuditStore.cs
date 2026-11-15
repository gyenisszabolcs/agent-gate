using System.Globalization;
using AgentGate.Policy;
using Microsoft.Data.Sqlite;

namespace AgentGate.Audit;

/// <summary>Önálló tároló előzetesen titokmentesített auditadatokhoz; nem végez szűrést.</summary>
public sealed class SqliteAuditStore
{
    private readonly string connectionString;

    public SqliteAuditStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (databasePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            !Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("Teljes, érvényes adatbázis-fájlútvonal szükséges.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        if (Directory.Exists(fullPath) || !Directory.Exists(Path.GetDirectoryName(fullPath)))
        {
            throw new ArgumentException("Az adatbázis meglévő szülőkönyvtárban lévő fájl lehet.", nameof(databasePath));
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public async Task AppendAsync(SanitizedAuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Tool);
        ArgumentNullException.ThrowIfNull(entry.SanitizedParameters);
        ArgumentNullException.ThrowIfNull(entry.SanitizedResult);
        if (entry.TimestampUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(entry.RiskLevel))
        {
            throw new ArgumentException("UTC időpont és ismert kockázati szint szükséges.", nameof(entry));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await InitializeAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AuditEntries (TimestampUtc, Caller, Tool, SanitizedParameters, RiskLevel, SanitizedResult)
            VALUES ($timestamp, $caller, $tool, $parameters, $risk, $result);
            """;
        command.Parameters.AddWithValue("$timestamp", entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$caller", entry.Caller);
        command.Parameters.AddWithValue("$tool", entry.Tool);
        command.Parameters.AddWithValue("$parameters", entry.SanitizedParameters);
        command.Parameters.AddWithValue("$risk", (int)entry.RiskLevel);
        command.Parameters.AddWithValue("$result", entry.SanitizedResult);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SanitizedAuditEntry>> ReadAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await InitializeAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TimestampUtc, Caller, Tool, SanitizedParameters, RiskLevel, SanitizedResult
            FROM AuditEntries ORDER BY Id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<SanitizedAuditEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new SanitizedAuditEntry(
                DateTimeOffset.ParseExact(reader.GetString(0), "O", CultureInfo.InvariantCulture),
                reader.GetString(1), reader.GetString(2), reader.GetString(3),
                (RiskLevel)reader.GetInt32(4), reader.GetString(5)));
        }

        return entries.AsReadOnly();
    }

    private static async Task InitializeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AuditEntries (
                Id INTEGER PRIMARY KEY,
                TimestampUtc TEXT NOT NULL,
                Caller TEXT NOT NULL,
                Tool TEXT NOT NULL,
                SanitizedParameters TEXT NOT NULL,
                RiskLevel INTEGER NOT NULL,
                SanitizedResult TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
