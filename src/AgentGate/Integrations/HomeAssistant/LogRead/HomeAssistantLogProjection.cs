using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentGate.Integrations.HomeAssistant.LogRead;

internal static class HomeAssistantLogProjection
{
    private const int MaximumOutputBytes = 32_000;

    public static CallToolResult Create(List<HomeAssistantLogEntry> entries, bool truncated, bool redacted, int withheld, CancellationToken cancellationToken)
    {
        var result = Build(entries, truncated, redacted, withheld);
        while (!Fits(result))
        {
            cancellationToken.ThrowIfCancellationRequested();
            truncated = true;
            if (entries.Count > 1)
            {
                // A legújabb bejegyzéseket tartjuk meg; nem a tömb végét vágjuk le.
                entries.RemoveAt(0);
                result = Build(entries, truncated, redacted, withheld);
                continue;
            }
            var entry = entries[0];
            var low = 0;
            var high = entry.Messages.Sum(message => message.Length) + entry.Exception.Length;
            result = Build([Clip(entry, 0)], true, redacted, withheld);
            // Ha már a metaadat és két másolata sem fér be, a szöveg vágása nem segíthet.
            if (!Fits(result)) return Build([], true, redacted, withheld);
            while (low <= high)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var middle = low + (high - low) / 2;
                var candidate = Build([Clip(entry, middle)], true, redacted, withheld);
                if (Fits(candidate))
                {
                    result = candidate;
                    low = middle + 1;
                }
                else high = middle - 1;
            }
            break;
        }
        return result;
    }

    // A protokoll szerializált CallToolResult-ja: a szöveges másolat ESCAPE-jei és a metaadatok is beleszámítanak.
    private static bool Fits(CallToolResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result, McpJsonUtilities.DefaultOptions).Length <= MaximumOutputBytes;

    private static HomeAssistantLogEntry Clip(HomeAssistantLogEntry entry, int length)
    {
        string Prefix(string value)
        {
            var keep = Math.Min(length, value.Length);
            if (keep > 0 && keep < value.Length && char.IsHighSurrogate(value[keep - 1]) && char.IsLowSurrogate(value[keep])) keep--;
            length -= keep;
            return value[..keep];
        }
        var messages = entry.Messages.Select(Prefix).ToArray();
        return entry with { Messages = messages, Exception = Prefix(entry.Exception) };
    }

    private static CallToolResult Build(IReadOnlyList<HomeAssistantLogEntry> entries, bool truncated, bool redacted, int withheld)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            items = entries.Select(item => new { logger = item.Logger, level = item.Level, integration = item.Integration,
                messages = item.Messages, count = item.Count, first_occurred = item.FirstOccurred,
                timestamp = item.Timestamp, exception = item.Exception }),
            returned_count = entries.Count,
            withheld_count = withheld,
            truncated,
            redacted
        });
        return new CallToolResult { StructuredContent = data, Content = [new TextContentBlock { Text = data.GetRawText() }] };
    }
}
