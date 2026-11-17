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
        return $"{SafeLine(request.Caller, 128, token)}\n{request.Tool}\nKockázat: {risk}\n{Parameters(request.Summary, token, request.Tool)}\nLejárat (Europe/Budapest): {expires}\nAzonosító: {request.Id.ToString("N")[..8]}"
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

    private static string Parameters(string summary, string token, string tool)
    {
        const string omitted = "[kihagyva]";
        if (summary.Length > 65536) return omitted;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(summary, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return omitted;
        }
        // A belső TTS-adatok hibája nem válhat hiányos, mégis jóváhagyható értesítéssé.
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return omitted;
            var root = document.RootElement;
            if (tool == "ha_sensitive_control" && root.TryGetProperty("domain", out var domain) && domain.ValueKind == JsonValueKind.String && domain.GetString() == "media_player" &&
                root.TryGetProperty("service", out var service) && service.ValueKind == JsonValueKind.String && service.GetString() == "speak")
                return TtsParameters(root, token);
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
    }

    private static string TtsParameters(JsonElement root, string token)
    {
        // A JSON-ba ágyazott JSON Unicode-escape-jei és az általános 512-es vágás
        // eltakarhatnák a jóváhagyandó szöveget/motort. TTS-nél nincs csonkítás.
        static InvalidOperationException Invalid() => new("A felolvasás jóváhagyási összefoglalója nem jeleníthető meg hiánytalanul.");
        if (root.EnumerateObject().Count() != 5) throw Invalid();
        string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Invalid();
        using var data = JsonDocument.Parse(Text("data"));
        var payload = data.RootElement;
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != 2 ||
            !payload.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String || message.GetString()!.Length > 200 ||
            !payload.TryGetProperty("volume_level", out var volume) || !Integrations.HomeAssistant.Control.ControlNumber.InRange(volume, 0, 1)) throw Invalid();
        var fields = new Dictionary<string, string>
        {
            ["domain"] = Text("domain"), ["service"] = Text("service"), ["entity_id"] = Text("entity_id"),
            ["tts_engine"] = Text("tts_engine"), ["volume_level"] = volume.GetRawText(), ["message"] = message.GetString()!
        };
        var lines = fields.Select(p => p.Key + ": " + SafeLine(p.Value, 8192, token));
        var text = string.Join('\n', lines);
        if (fields.Values.Any(v => v.Length > 8192) || text.Length > 8192) throw Invalid();
        return text;
    }
}
