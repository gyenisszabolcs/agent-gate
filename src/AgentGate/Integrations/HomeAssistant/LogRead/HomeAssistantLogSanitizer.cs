using System.Globalization;
using System.Text.RegularExpressions;
using AgentGate.Secrets;

namespace AgentGate.Integrations.HomeAssistant.LogRead;

public sealed record HomeAssistantLogSnapshot(string Text, bool Redacted);

internal static class HomeAssistantLogSanitizer
{
    // Szándékos túltiltás: érzékeny jelölőnél nem találgatjuk a több soros érték végét.
    private static readonly Regex SensitiveMarker = new(
        "token|password|passwd|secret|authorization|credential|cookie|key",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    public static HomeAssistantLogSnapshot Sanitize(string text, string knownToken)
    {
        // Mindig a TELJES bemeneten, bármilyen darabolás és méretvágás előtt.
        var sanitized = SecretRedactor.RedactText(text)!.Replace(knownToken, "[REDACTED]", StringComparison.Ordinal);
        if (sanitized == "[REDACTED]") return new("", true);
        // Az ismert token sor-/Unicode-térközzel széttört alakját is zártan kezeljük.
        // Ez nem dekóder vagy általános DLP; az ismeretlen titkok felismerése heurisztikus marad.
        var remainder = text.Replace(knownToken, "[REDACTED]", StringComparison.Ordinal);
        var compact = new string(remainder.Where(character => !char.IsWhiteSpace(character) && !char.IsControl(character) &&
            char.GetUnicodeCategory(character) != UnicodeCategory.Format).ToArray());
        try
        {
            if (SensitiveMarker.IsMatch(compact) || compact.Contains(knownToken, StringComparison.Ordinal))
                return new("", true);
        }
        catch (RegexMatchTimeoutException)
        {
            return new("", true);
        }
        return new(sanitized, false);
    }
}
