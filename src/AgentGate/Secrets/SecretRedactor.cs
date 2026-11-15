using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentGate.Secrets;

/// <summary>Érzékeny paraméterértékek és szövegrészletek heurisztikus kitakarása.</summary>
/// <remarks>Nem általános titokfelismerő. A formátumkorlátokat a docs/titokszuro.md írja le.</remarks>
public static class SecretRedactor
{
    private const string Replacement = "[REDACTED]";
    private const int MaxTextLength = 65_536;
    private static readonly Regex SensitiveName = new(
        @"token|password|secret|authorization|(?:^|[_.\-\s])key(?:$|[_.\-\s])|(?:api|access|private|public|signing|encryption|ssh|session|client|consumer|subscription)key",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    private static readonly Regex TextAssignment = new(
        """(?<prefix>["']?(?<name>[A-Za-z_][A-Za-z0-9_.-]*)["']?[ \t]*[:=][ \t]*)(?<value>"(?:\\[\s\S]|[^"\\])*"|'(?:\\[\s\S]|[^'\\])*'|[^\s,;&{}\[\]"']+)""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    /// <summary>A felismert értékeket elfedi; túlméretes bemenetnél vagy időtúllépésnél mindent kitakar.</summary>
    public static string? RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }
        if (text.Length > MaxTextLength)
        {
            // Túl nagy bemenetet feldolgozás és részleges visszaadás helyett teljesen elfedünk.
            return Replacement;
        }
        try
        {
            var result = new StringBuilder(text.Length);
            var position = 0;
            for (var match = TextAssignment.Match(text); match.Success; match = TextAssignment.Match(text, position))
            {
                result.Append(text, position, match.Index - position);
                position = match.Index + match.Length;
                if (!IsSensitiveName(match.Groups["name"].Value))
                {
                    result.Append(match.Value);
                    continue;
                }
                var value = match.Groups["value"].Value;
                // Csak érzékeny mezőnél fogyasztjuk el a séma utáni értéket.
                // A kurzor csak előre halad: nincs rekurzió vagy újrafeldolgozás.
                if (value.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("Basic", StringComparison.OrdinalIgnoreCase))
                {
                    var credentialStart = position;
                    while (credentialStart < text.Length && text[credentialStart] is ' ' or '\t')
                    {
                        credentialStart++;
                    }
                    var credentialEnd = credentialStart;
                    while (credentialEnd < text.Length && !char.IsWhiteSpace(text[credentialEnd])
                        && text[credentialEnd] is not (',' or ';' or '&' or '{' or '}' or '[' or ']' or '"' or '\''))
                    {
                        credentialEnd++;
                    }
                    if (credentialStart > position && credentialEnd > credentialStart)
                    {
                        var nextIsAssignment = StartsAssignment(text, credentialStart, credentialEnd, out var ambiguousPadding);
                        if (ambiguousPadding && match.Groups["name"].Value.Contains("authorization", StringComparison.OrdinalIgnoreCase))
                        {
                            // A paddinges credential és a térközös mezőérték kétértelmű:
                            // Authorization esetén a lehetséges credential nevét sem hagyjuk meg.
                            return Replacement;
                        }
                        if (!nextIsAssignment)
                        {
                            position = credentialEnd;
                        }
                    }
                }
                var replacement = value[0] is '\"' or '\''
                    ? value[0] + Replacement + value[0]
                    : Replacement;
                result.Append(match.Groups["prefix"].Value).Append(replacement);
            }
            return result.Append(text, position, text.Length - position).ToString();
        }
        catch (RegexMatchTimeoutException)
        {
            // A kivétel bemenetet tartalmazhat, ezért nem továbbítjuk és nem naplózzuk.
            return Replacement;
        }
    }

    private static bool StartsAssignment(string text, int start, int tokenEnd, out bool ambiguousPadding)
    {
        ambiguousPadding = false;
        var separator = FindAssignmentSeparator(text, start);
        if (separator < 0)
        {
            return false;
        }
        // A token68 credential végén álló = / == padding nem mezőkezdet,
        // ha utána nincs érték, vagy már egy további mező kezdődik.
        if (text[separator] == '=' && separator < tokenEnd)
        {
            var paddingEnd = separator;
            while (paddingEnd < tokenEnd && text[paddingEnd] == '=')
            {
                paddingEnd++;
            }
            if (paddingEnd == tokenEnd)
            {
                var next = tokenEnd;
                while (next < text.Length && text[next] is ' ' or '\t')
                {
                    next++;
                }
                var hasValue = next < text.Length
                    && text[next] is not (',' or ';' or '&' or '{' or '}' or '[' or ']')
                    && !char.IsWhiteSpace(text[next])
                    && FindAssignmentSeparator(text, next) < 0;
                ambiguousPadding = hasValue;
                return hasValue;
            }
        }
        return true;
    }

    private static int FindAssignmentSeparator(string text, int start)
    {
        var cursor = start;
        var quote = cursor < text.Length && text[cursor] is '"' or '\'' ? text[cursor++] : '\0';
        if (cursor >= text.Length || !(char.IsAsciiLetter(text[cursor]) || text[cursor] == '_'))
        {
            return -1;
        }
        while (cursor < text.Length && (char.IsAsciiLetterOrDigit(text[cursor])
            || text[cursor] is '_' or '.' or '-'))
        {
            cursor++;
        }
        if (quote != '\0')
        {
            if (cursor >= text.Length || text[cursor] != quote)
            {
                return -1;
            }
            cursor++;
        }
        while (cursor < text.Length && text[cursor] is ' ' or '\t')
        {
            cursor++;
        }
        return cursor < text.Length && text[cursor] is ':' or '=' ? cursor : -1;
    }

    private static bool IsSensitiveName(string name)
    {
        try
        {
            return SensitiveName.IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            // A név ellenőrzésének időtúllépésekor a teljes értéket takarjuk ki.
            return true;
        }
    }

    /// <summary>Új, csak olvasható pillanatképet ad vissza, az érzékeny nevű mezők értékei nélkül.</summary>
    public static IReadOnlyDictionary<string, string?> RedactParameters(IReadOnlyDictionary<string, string?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            result.Add(parameter.Key, IsSensitiveName(parameter.Key)
                ? Replacement
                : parameter.Value);
        }
        return new ReadOnlyDictionary<string, string?>(result);
    }
}
