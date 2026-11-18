using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentGate.Integrations.HomeAssistant.LogRead;

internal static class HomeAssistantLogProjection
{
    private const int MaximumOutputBytes = 32_000;

    public static CallToolResult Create(List<HomeAssistantLogEntry> entries, bool truncated, bool redacted, CancellationToken cancellationToken)
    {
        var result = Build(entries, truncated, redacted);
        while (!Fits(result))
        {
            cancellationToken.ThrowIfCancellationRequested();
            truncated = true;
            if (entries.Count > 1)
            {
                // A legújabb bejegyzéseket tartjuk meg; nem a tömb végét vágjuk le.
                entries.RemoveAt(0);
                result = Build(entries, truncated, redacted);
                continue;
            }
            var entry = entries[0];
            var low = 0;
            var high = entry.Text.Length;
            result = Build([entry with { Text = "" }], true, redacted);
            // Ha már a metaadat és két másolata sem fér be, a szöveg vágása nem segíthet.
            if (!Fits(result)) return Build([], true, redacted);
            while (low <= high)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var middle = low + (high - low) / 2;
                var length = middle > 0 && middle < entry.Text.Length && char.IsHighSurrogate(entry.Text[middle - 1]) &&
                    char.IsLowSurrogate(entry.Text[middle]) ? middle - 1 : middle;
                var candidate = Build([entry with { Text = entry.Text[..length] }], true, redacted);
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

    private static CallToolResult Build(IReadOnlyList<HomeAssistantLogEntry> entries, bool truncated, bool redacted)
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            items = entries.Select(item => new { timestamp = item.Timestamp, level = item.Level, integration = item.Integration, text = item.Text }),
            returned_count = entries.Count,
            truncated,
            redacted
        });
        return new CallToolResult { StructuredContent = data, Content = [new TextContentBlock { Text = data.GetRawText() }] };
    }
}
