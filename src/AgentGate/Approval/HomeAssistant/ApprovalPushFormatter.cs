using System.Globalization;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Policy;
using AgentGate.Secrets;

namespace AgentGate.Approval.HomeAssistant;

/// <summary>Kizárólag megjelenítés: a szűrt összefoglalót és a külön előkészített emberi neveket olvassa.</summary>
public sealed partial class ApprovalPushFormatter(Func<TimeZoneInfo>? timeZone = null)
{
    public string Request(ApprovalNotice request, string token) => Format(request, token).Message;

    public (string Title, string Message) Format(ApprovalNotice request, string token)
    {
        var zone = (timeZone ?? (() => TimeZoneInfo.FindSystemTimeZoneById("Europe/Budapest")))();
        var expires = TimeZoneInfo.ConvertTime(request.ExpiresAt, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        if (request.Risk is not (RiskLevel.SensitiveControl or RiskLevel.Configuration or RiskLevel.System))
            throw new InvalidOperationException("A jóváhagyási kérés nem küldhető.");
        var target = ApprovalPresentation.IsHumanName(request.Presentation?.TargetName, token)
            ? request.Presentation!.TargetName! : "Kiválasztott eszköz";
        var action = request.Tool == "gate_approval_test" ? "próba" : "művelet";
        if (request.Tool == "ha_sensitive_control")
        {
            action = SensitiveAction(request.Summary);
        }
        if (request.Tool == "ha_update_registry")
        {
            var display = request.Presentation ?? throw ApprovalPresentation.CannotDisplay();
            ValidateRegistrySummary(request.Summary, display, token);
            target = display.RegistryLine(token);
            action = display.Changes.Count == 1 ? display.Changes.Keys.Single() switch
            {
                "name" => "név módosítása", "area_id" => "terület módosítása", "labels" => "címkék módosítása",
                "hidden_by" => "elrejtés módosítása", "disabled_by" => "letiltás módosítása",
                _ => throw ApprovalPresentation.CannotDisplay()
            } : "nyilvántartás módosítása";
        }
        return ($"Jóváhagyás: {action}", $"{target}\nMikaela kéri · lejár {expires}");
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

    private static string SensitiveAction(string summary)
    {
        // A kötött művelet validálása megmarad; sem a TTS-szöveget, sem az azonosítókat nem formázzuk meg.
        if (summary.Length > 65536) throw InvalidSummary();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(summary, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            throw InvalidSummary();
        }
        // A belső TTS-adatok hibája nem válhat hiányos, mégis jóváhagyható értesítéssé.
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw InvalidSummary();
            var root = document.RootElement;
            var fields = root.EnumerateObject().ToArray();
            if (fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length) throw InvalidSummary();
            var domain = Text(root, "domain");
            var service = Text(root, "service");
            var entity = Text(root, "entity_id");
            if (!ControlPolicy.CanonicalEntity(entity) || !entity.StartsWith(domain + ".", StringComparison.Ordinal)) throw InvalidSummary();
            if (domain == "media_player" && service == "speak")
            {
                ValidateTts(root);
                return "felolvasás";
            }
            if (fields.Length != 4) throw InvalidSummary();
            using var data = JsonDocument.Parse(Text(root, "data"), new JsonDocumentOptions { MaxDepth = 16 });
            if (data.RootElement.ValueKind != JsonValueKind.Object || data.RootElement.EnumerateObject().Any()) throw InvalidSummary();
            return (domain, service) switch
            {
                ("lock", "lock") => "zár bezárása", ("lock", "unlock") => "zár nyitása",
                ("cover", "open_cover") => "nyitás", ("cover", "close_cover") => "zárás",
                ("cover", "stop_cover") => "mozgás leállítása", _ => throw InvalidSummary()
            };
        }
    }

    private static InvalidOperationException InvalidSummary() => new("A jóváhagyási összefoglaló nem jeleníthető meg hiánytalanul.");

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw InvalidSummary();

    private static void ValidateTts(JsonElement root)
    {
        var engine = Text(root, "tts_engine");
        if (!ControlPolicy.CanonicalEntity(engine) || !engine.StartsWith("tts.", StringComparison.Ordinal)) throw InvalidSummary();
        using var data = JsonDocument.Parse(Text(root, "data"), new JsonDocumentOptions { MaxDepth = 16 });
        var payload = data.RootElement;
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String || message.GetString()!.Length is < 1 or > 200) throw InvalidSummary();
        var explicitVolume = payload.TryGetProperty("volume_level", out var volume);
        if (payload.EnumerateObject().Count() != (explicitVolume ? 2 : 1) || root.EnumerateObject().Count() != (explicitVolume ? 5 : 7)) throw InvalidSummary();

        if (explicitVolume)
        {
            if (!ControlNumber.InRange(volume, 0, 1)) throw InvalidSummary();
        }
        else
        {
            using var maximum = JsonDocument.Parse(Text(root, "tts_max_volume"));
            using var fallback = JsonDocument.Parse(Text(root, "tts_fallback_volume"));
            if (!ControlNumber.InRange(maximum.RootElement, 0, 1) || !ControlNumber.InRange(fallback.RootElement, 0, 1) ||
                ControlNumber.Compare(fallback.RootElement, JsonSerializer.SerializeToElement(0)) <= 0 ||
                ControlNumber.Compare(fallback.RootElement, maximum.RootElement) > 0) throw InvalidSummary();
        }
    }
}
