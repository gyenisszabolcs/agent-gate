using System.Globalization;
using System.Text.RegularExpressions;
using AgentGate.Secrets;

namespace AgentGate.Integrations.HomeAssistant.LogRead;

internal static class HomeAssistantLogSanitizer
{
    // Csak szóhatár és jellemző alak: KeyError, keyboard és tokenizer önmagukban nem titokjelölők.
    private static readonly Regex SensitiveMarker = new(
        @"(?:\b|_)(?:api[_-]?key|access[_-]?token|token|password|passwd|secret|authorization|credentials?|cookie|bearer)(?:\b|_)|(?:\b|_)key[""']?\s*[:=]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    public static HomeAssistantLogEntry? Sanitize(HomeAssistantLogEntry entry, string knownToken)
    {
        var fields = new[] { entry.Logger, entry.Level, entry.Integration ?? "" }.Concat(entry.Messages).Append(entry.Exception).ToArray();
        // Teljes dekódolt bejegyzés, vágás előtt. Az egybefűzés a mezők közé rejtett ismert tokent is védi.
        var text = string.Join('\n', fields);
        var sanitized = SecretRedactor.RedactText(text)!;
        if (sanitized == "[REDACTED]") return null;
        // A származtatott integration és a kötött level nem ékelődhet a forrásmezők töredékei közé.
        var sourceFields = new[] { entry.Logger }.Concat(entry.Messages).Append(entry.Exception);
        var remainder = string.Concat(sourceFields.Select(field => field.Replace(knownToken, "[REDACTED]", StringComparison.Ordinal)));
        var compact = new string(remainder.Where(character => !char.IsWhiteSpace(character) && !char.IsControl(character) &&
            char.GetUnicodeCategory(character) != UnicodeCategory.Format).ToArray());
        try
        {
            if (SensitiveMarker.IsMatch(text) || compact.Contains(knownToken, StringComparison.Ordinal)) return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        string Clean(string field) => SecretRedactor.RedactText(field)!.Replace(knownToken, "[REDACTED]", StringComparison.Ordinal);
        // A közös szűrő átívelhet mezőhatáron (például idézett, több üzenetes érték).
        // Ilyenkor a külön mezők tisztítása nem bizonyítja ugyanazt a védelmet: az egész rekord visszatartott.
        if (string.Join('\n', fields.Select(Clean)) != sanitized.Replace(knownToken, "[REDACTED]", StringComparison.Ordinal)) return null;
        var logger = Clean(entry.Logger);
        return entry with { Logger = logger, Integration = HomeAssistantLogParser.Integration(logger),
            Messages = entry.Messages.Select(Clean).ToArray(), Exception = Clean(entry.Exception) };
    }
}
