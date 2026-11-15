using System.Globalization;
using System.Text.Json;
using AgentGate.Policy;
using AgentGate.Secrets;

namespace AgentGate.Approval.HomeAssistant;

/// <summary>Kizárólag megjelenítés: az audit engedélyezőlistás összefoglalóját olvassa, nem a kötött műveletet.</summary>
public sealed class ApprovalPushFormatter(Func<TimeZoneInfo>? timeZone = null)
{
    public string Request(ApprovalNotice request, string token)
    {
        var zone = (timeZone ?? (() => TimeZoneInfo.FindSystemTimeZoneById("Europe/Budapest")))();
        var expires = TimeZoneInfo.ConvertTime(request.ExpiresAt, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var risk = request.Risk switch
        {
            RiskLevel.SensitiveControl => "érzékeny vezérlés",
            RiskLevel.Configuration => "konfiguráció",
            RiskLevel.System => "rendszer",
            _ => throw new InvalidOperationException("A jóváhagyási kérés nem küldhető.")
        };
        return $"{SafeLine(request.Caller, 128, token)}\n{request.Tool}\nKockázat: {risk}\n{Parameters(request.Summary, token)}\nLejárat (Europe/Budapest): {expires}\nAzonosító: {request.Id.ToString("N")[..8]}"
            .Replace(token, "[REDACTED]", StringComparison.Ordinal);
    }

    public static string SafeLine(string value, int limit, string token)
    {
        // Az ismert token eltávolítása a soronkénti és az összesített vágást is megelőzi.
        var safe = SecretRedactor.RedactText(value.Replace(token, "[REDACTED]", StringComparison.Ordinal))!;
        safe = string.Concat(safe.Select(character => char.IsControl(character) ||
            char.GetUnicodeCategory(character) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            ? ' ' : character));
        return safe.Length <= limit ? safe : safe[..limit];
    }

    private static string Parameters(string summary, string token)
    {
        const string omitted = "[kihagyva]";
        if (summary.Length > 65536) return omitted;
        try
        {
            using var document = JsonDocument.Parse(summary, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return omitted;
            var lines = new List<string>();
            foreach (var field in document.RootElement.EnumerateObject())
            {
                var value = field.Value.ValueKind switch
                {
                    JsonValueKind.String => field.Value.GetString(),
                    JsonValueKind.Null => "[nincs érték]",
                    _ => omitted
                };
                var filtered = SecretRedactor.RedactParameters(new Dictionary<string, string?> { [field.Name] = value });
                lines.Add($"{SafeLine(field.Name, 512, token)}: {SafeLine(filtered[field.Name] ?? "[nincs érték]", 512, token)}");
            }
            // A sorhatárok csak innen származnak, nem a mezők tartalmából.
            var text = lines.Count == 0 ? "[nincs paraméter]" : string.Join('\n', lines);
            return text.Length <= 512 ? text : text[..512];
        }
        catch (JsonException)
        {
            return omitted;
        }
    }
}
