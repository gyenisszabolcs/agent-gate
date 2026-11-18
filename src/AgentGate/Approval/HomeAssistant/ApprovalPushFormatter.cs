using System.Globalization;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.Control;
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
        var (action, parameters) = Parameters(request.Summary, token, request.Tool);
        return $"{action}\nKérő: {SafeLine(request.Caller, 128, token)}\nKockázat: {risk}\nLejárat (Europe/Budapest): {expires}\nAzonosító: {request.Id.ToString("N")[..8]}\n{parameters}\nEszköz: {SafeLine(request.Tool, 128, token)}"
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

    private static (string Action, string Details) Parameters(string summary, string token, string tool)
    {
        const string omitted = "[kihagyva]";
        var action = tool == "gate_approval_test"
            ? "Jóváhagyási próba végrehajtása éles mellékhatás nélkül."
            : "Jóváhagyást igénylő művelet.";
        // Az ismert érzékeny művelet hiányos összefoglalója nem kaphat általános címet.
        (string, string) Fallback() => tool == "ha_sensitive_control" ? throw InvalidSummary() : (action, omitted);
        if (summary.Length > 65536) return Fallback();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(summary, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return Fallback();
        }
        // A belső TTS-adatok hibája nem válhat hiányos, mégis jóváhagyható értesítéssé.
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Fallback();
            var root = document.RootElement;
            if (tool == "ha_sensitive_control")
            {
                var fields = root.EnumerateObject().ToArray();
                if (fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length) throw InvalidSummary();
                var domain = Text(root, "domain");
                var service = Text(root, "service");
                var entity = Text(root, "entity_id");
                if (!ControlPolicy.CanonicalEntity(entity) || !entity.StartsWith(domain + ".", StringComparison.Ordinal)) throw InvalidSummary();
                if (domain == "media_player" && service == "speak") return TtsParameters(root, token);
                if (fields.Length != 4) throw InvalidSummary();
                using var data = JsonDocument.Parse(Text(root, "data"), new JsonDocumentOptions { MaxDepth = 16 });
                if (data.RootElement.ValueKind != JsonValueKind.Object || data.RootElement.EnumerateObject().Any()) throw InvalidSummary();
                var target = SafeLine(entity, 255, token);
                action = (domain, service) switch
                {
                    ("lock", "lock") => $"A(z) {target} zár bezárása.",
                    ("lock", "unlock") => $"A(z) {target} zár nyitása.",
                    ("cover", "open_cover") => $"A(z) {target} nyitása.",
                    ("cover", "close_cover") => $"A(z) {target} zárása.",
                    ("cover", "stop_cover") => $"A(z) {target} mozgásának leállítása.",
                    _ => throw InvalidSummary()
                };
            }
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
            return (action, text.Length <= 512 ? text : text[..512]);
        }
    }

    private static InvalidOperationException InvalidSummary() => new("A jóváhagyási összefoglaló nem jeleníthető meg hiánytalanul.");

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw InvalidSummary();

    private static (string Action, string Details) TtsParameters(JsonElement root, string token)
    {
        // A JSON-ba ágyazott JSON Unicode-escape-jei és az általános 512-es vágás
        // eltakarhatnák a jóváhagyandó szöveget/motort. TTS-nél nincs csonkítás.
        if (root.EnumerateObject().Count() != 5) throw InvalidSummary();
        var engine = Text(root, "tts_engine");
        if (!ControlPolicy.CanonicalEntity(engine) || !engine.StartsWith("tts.", StringComparison.Ordinal)) throw InvalidSummary();
        using var data = JsonDocument.Parse(Text(root, "data"), new JsonDocumentOptions { MaxDepth = 16 });
        var payload = data.RootElement;
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Count() != 2 ||
            !payload.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String || message.GetString()!.Length is < 1 or > 200 ||
            !payload.TryGetProperty("volume_level", out var volume) || !ControlNumber.InRange(volume, 0, 1)) throw InvalidSummary();
        var fields = new Dictionary<string, string>
        {
            ["domain"] = Text(root, "domain"), ["service"] = Text(root, "service"), ["entity_id"] = Text(root, "entity_id"),
            ["tts_engine"] = engine, ["volume_level"] = volume.GetRawText(), ["message"] = message.GetString()!
        };
        var lines = fields.Select(p => p.Key + ": " + SafeLine(p.Value, 8192, token));
        var text = string.Join('\n', lines);
        if (fields.Values.Any(v => v.Length > 8192) || text.Length > 8192) throw InvalidSummary();
        var action = $"Felolvasás a(z) {SafeLine(fields["entity_id"], 8192, token)} hangszórón: »{SafeLine(fields["message"], 8192, token)}«";
        if (action.Length > 1024) throw InvalidSummary();
        return (action, text);
    }
}
