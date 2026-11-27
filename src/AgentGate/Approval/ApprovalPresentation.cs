using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using AgentGate.Secrets;
using AgentGate.Tools;

namespace AgentGate.Approval;

/// <summary>Csak megjelenítés; nem része a paramétereknek, a hash-nek vagy a jogosultságvizsgálatnak.</summary>
public sealed class ApprovalPresentation(string? targetName, IReadOnlyDictionary<string, ApprovalDisplayChange>? changes = null,
    IReadOnlyList<ApprovalPresentation>? items = null, string? entityIdFallback = null)
{
    public IReadOnlyList<ApprovalPresentation>? Items { get; } = items is null ? null : Array.AsReadOnly(items.ToArray());
    public string? TargetName { get; } = targetName;
    public string? EntityIdFallback { get; } = entityIdFallback;
    public FrozenDictionary<string, ApprovalDisplayChange> Changes { get; } =
        (changes ?? new Dictionary<string, ApprovalDisplayChange>()).ToFrozenDictionary(StringComparer.Ordinal);

    public const string SplitRequestMessage = "A teljes változás nem fér rövid jóváhagyásba. Bontsd kisebb kérésekre.";
    public const string InvalidDisplayMessage = "A jóváhagyási megjelenítés hiányos, nem egyértelmű vagy nem támogatott.";
    public const string InvalidNameMessage = "A jóváhagyási név nem biztonságos vagy érvénytelen.";
    public const string LongNameMessage = "A jóváhagyási név túl hosszú (legfeljebb 48 szövegelem és 192 karakter).";
    public const string FullLimitMessage = "A teljes jóváhagyási változás meghaladja a 32 KiB korlátot. Bontsd kisebb kérésekre.";
    public const string RowLimitMessage = "A jóváhagyási változás egy sora meghaladja a 8192 karakteres korlátot. Bontsd kisebb kérésekre.";
    public static GateToolException CannotDisplay() => new(InvalidDisplayMessage);
    public static GateToolException TooLarge(bool full = false) => new(full ? FullLimitMessage : SplitRequestMessage);

    // Nem csonkítunk vagy helyettesítünk részleteket. A cél neve helyett a formatter használhat fix semleges nevet.
    public static bool IsHumanName(string? value, string token = "")
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || new StringInfo(value).LengthInTextElements > 48 ||
            value.Length > 192 || SecretRedactor.RedactText(value) != value ||
            token.Length > 0 && value.Contains(token, StringComparison.Ordinal) || Guid.TryParse(value, out _) ||
            value.Length >= 16 && value.All(Uri.IsHexDigit)) return false;
        var hasLetter = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == 0xfffd) return false;
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetter(rune)) hasLetter = true;
            if (category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.OtherSymbol ||
                rune.Value is ' ' or '-' or '\'' or '(' or ')') continue;
            return false;
        }
        return hasLetter;
    }

    public static string RequireName(string? value, string token = "")
    {
        if (value is not null && (value.Length > 192 || new StringInfo(value).LengthInTextElements > 48))
            throw new GateToolException(LongNameMessage);
        return IsHumanName(value, token) ? value! : throw new GateToolException(InvalidNameMessage);
    }

    public string RequireRegistryTarget(string token = "") => EntityIdFallback is null ? RequireName(TargetName, token) :
        TargetName == EntityIdFallback && Integrations.HomeAssistant.Control.ControlPolicy.CanonicalEntity(EntityIdFallback) &&
        SecretRedactor.RedactText(EntityIdFallback) == EntityIdFallback &&
        (token.Length == 0 || !EntityIdFallback.Contains(token, StringComparison.Ordinal)) ? EntityIdFallback : throw CannotDisplay();

    public string RegistryLine(string token = "", bool extended = false)
    {
        if (Changes.ContainsKey("new_label"))
        {
            var creation = Integrations.HomeAssistant.Registry.LabelCreation.Display(this, token);
            if (!extended && new StringInfo(creation).LengthInTextElements > 160) throw TooLarge();
            return creation;
        }
        if (Changes.Count == 0) throw CannotDisplay();
        var parts = Changes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var label = FieldName(pair.Key);
            var before = RequireValue(pair.Value.Before, token);
            var after = RequireValue(pair.Value.After, token);
            return $"{label}: {before} → {after}";
        });
        var target = RequireRegistryTarget(token);
        var line = target + " · " + string.Join("; ", parts);
        if (extended && line.Length > 8192) throw new GateToolException(RowLimitMessage);
        if (!extended && (new StringInfo(line).LengthInTextElements > 160 || line.Length > 640)) throw TooLarge();
        return line;
    }

    private static string RequireValue(string value, string token)
    {
        // A listát csak az előkészítő állítja össze, minden eleme külön ellenőrzött emberi név.
        if (value.Split(", ", StringSplitOptions.None).Any(part => !IsHumanName(part, token))) throw CannotDisplay();
        return value;
    }

    public static string FieldName(string field) => field switch
    {
        "name" or "name_by_user" => "Név", "area_id" => "Terület", "labels" => "Címkék",
        "hidden_by" => "Elrejtés", "disabled_by" => "Letiltás",
        _ => throw CannotDisplay()
    };
}

public sealed record ApprovalDisplayChange(string Before, string After);
