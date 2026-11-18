using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentGate.Integrations.HomeAssistant.LogRead;

internal sealed record HomeAssistantLogEntry(string? Timestamp, string? Level, string? Integration, string Text);

internal static class HomeAssistantLogParser
{
    private static readonly Regex Header = new(
        @"\A(?<time>[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}(?:[+-][0-9]{2}:[0-9]{2})?) (?<level>DEBUG|INFO|WARNING|ERROR|CRITICAL) \([^\r\n()]+\) \[(?<logger>[a-zA-Z0-9_.]+)\] ",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<HomeAssistantLogEntry> Parse(string text, CancellationToken cancellationToken)
    {
        var entries = new List<HomeAssistantLogEntry>();
        var body = new StringBuilder();
        string? timestamp = null, level = null, integration = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = Header.Match(line);
            var recognized = match.Success && DateTimeOffset.TryParse(match.Groups["time"].Value,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            if (recognized)
            {
                Flush();
                timestamp = match.Groups["time"].Value;
                level = match.Groups["level"].Value;
                var logger = match.Groups["logger"].Value;
                var parts = logger.Split('.');
                integration = parts.Length >= 3 && parts[0] == "homeassistant" && parts[1] == "components" ? parts[2] :
                    parts.Length >= 2 && parts[0] == "custom_components" ? parts[1] : null;
            }
            if (body.Length > 0) body.Append('\n');
            body.Append(line);
        }
        Flush();
        return entries;

        void Flush()
        {
            if (body.Length > 0) entries.Add(new(timestamp, level, integration, body.ToString()));
            body.Clear();
        }
    }
}
