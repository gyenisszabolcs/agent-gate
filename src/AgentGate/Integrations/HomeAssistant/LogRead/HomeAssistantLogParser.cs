using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentGate.Integrations.HomeAssistant.LogRead;

public sealed record HomeAssistantLogEntry(string Logger, string Level, string? Integration, string[] Messages,
    long Count, double FirstOccurred, double Timestamp, string Exception);

public sealed record HomeAssistantLogSnapshot(IReadOnlyList<HomeAssistantLogEntry> Items, int WithheldCount, bool Redacted);

internal static class HomeAssistantLogParser
{
    public static HomeAssistantLogSnapshot Parse(JsonElement result, string token, string? level, string? integration,
        CancellationToken cancellationToken)
    {
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > 1_024) throw new InvalidOperationException();
        var entries = new List<HomeAssistantLogEntry>();
        var withheld = 0;
        var redacted = false;
        foreach (var item in result.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logger = String(item, "name");
            var severity = String(item, "level");
            if (severity is not ("WARNING" or "ERROR" or "CRITICAL")) throw new InvalidOperationException();
            var messages = item.GetProperty("message");
            if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() is < 1 or > 5) throw new InvalidOperationException();
            var texts = messages.EnumerateArray().Select(value => value.GetString() ?? throw new InvalidOperationException()).ToArray();
            var exception = String(item, "exception");
            if (!item.GetProperty("count").TryGetInt64(out var count) || count < 1) throw new InvalidOperationException();
            var first = Number(item, "first_occurred");
            var last = Number(item, "timestamp");
            if (first > last) throw new InvalidOperationException();
            var component = Integration(logger);
            // Sémát minden rekordra ellenőrzünk; a visszatartás számlálója csak a szűrő találataira vonatkozik.
            if ((level is not null && level != severity) || (integration is not null && integration != component)) continue;
            var entry = new HomeAssistantLogEntry(logger, severity, component, texts, count, first, last, exception);
            var safe = HomeAssistantLogSanitizer.Sanitize(entry, token);
            if (safe is null)
            {
                withheld++;
                redacted = true;
                continue;
            }
            redacted |= safe.Logger != entry.Logger || safe.Exception != entry.Exception || !safe.Messages.SequenceEqual(entry.Messages);
            entries.Add(safe);
        }
        // Core DedupStore.to_list: LIFO. Nem időbélyeg szerint rendezünk: a forrássorrendet fordítjuk meg.
        entries.Reverse();
        return new(entries, withheld, redacted);
    }

    private static string String(JsonElement item, string name) =>
        item.GetProperty(name).GetString() ?? throw new InvalidOperationException();

    private static double Number(JsonElement item, string name)
    {
        var value = item.GetProperty(name).GetDouble();
        if (!double.IsFinite(value) || value < 0) throw new InvalidOperationException();
        return value;
    }

    internal static string? Integration(string logger)
    {
        var parts = logger.Split('.');
        var value = parts.Length >= 3 && parts[0] == "homeassistant" && parts[1] == "components" ? parts[2] :
            parts.Length >= 2 && parts[0] == "custom_components" ? parts[1] : null;
        return value is not null && Regex.IsMatch(value, @"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant) ? value : null;
    }
}
