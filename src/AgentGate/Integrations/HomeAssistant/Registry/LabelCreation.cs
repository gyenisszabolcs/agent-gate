using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentGate.Approval;

namespace AgentGate.Integrations.HomeAssistant.Registry;

internal static class LabelCreation
{
    private static readonly HashSet<string> Colors = new("primary accent disabled red pink purple deep-purple indigo blue light-blue cyan teal green light-green lime yellow amber orange deep-orange brown light-grey grey dark-grey blue-grey black white".Split(' '), StringComparer.Ordinal);

    public static SortedDictionary<string, object?> Intent(JsonElement value)
    {
        RegistrySocket.ValidateUnique(value);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Name is not ("name" or "icon" or "color")) || !value.TryGetProperty("name", out var name)) throw RegistryPolicy.Invalid();
        var text = RegistryPolicy.Text(name, 80, false)!;
        _ = ApprovalPresentation.RequireName(text);
        // NFC-szöveget kötünk: a diff, a hash és a HA-nak küldött név is ez.
        text = text.Normalize(NormalizationForm.FormC);
        return new(StringComparer.Ordinal)
        {
            ["name"] = text,
            ["icon"] = value.TryGetProperty("icon", out var icon) ? RegistryPolicy.Icon(icon) : null,
            ["color"] = value.TryGetProperty("color", out var color) ? Color(color) : null
        };
    }

    private static string? Color(JsonElement value)
    {
        var text = RegistryPolicy.Text(value, 32);
        if (text is not null && !Colors.Contains(text) && !Regex.IsMatch(text, @"\A#[0-9a-fA-F]{6}\z", RegexOptions.CultureInvariant)) throw RegistryPolicy.Invalid();
        return text;
    }

    public static string NameKey(string name) => UnicodeCaseFold.Fold(name.Normalize(NormalizationForm.FormC)).Replace(" ", "", StringComparison.Ordinal);

    public static JsonElement Catalog(JsonElement source)
    {
        var rows = RegistryPolicy.List(source).Select(Row).ToArray();
        if (rows.Select(row => (string)row["label_id"]!).Distinct(StringComparer.Ordinal).Count() != rows.Length ||
            rows.Select(row => NameKey((string)row["name"]!)).Distinct(StringComparer.Ordinal).Count() != rows.Length) throw RegistryPolicy.Invalid();
        return JsonSerializer.SerializeToElement(rows.OrderBy(row => (string)row["label_id"]!, StringComparer.Ordinal));
    }

    public static SortedDictionary<string, object?> Row(JsonElement value)
    {
        RegistrySocket.ValidateUnique(value);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Name is not ("label_id" or "name" or "icon" or "color" or "description" or "created_at" or "modified_at"))) throw RegistryPolicy.Invalid();
        foreach (var field in new[] { "created_at", "modified_at" })
            if (value.TryGetProperty(field, out var time) && (time.ValueKind != JsonValueKind.Number || !time.TryGetDouble(out var number) || !double.IsFinite(number))) throw RegistryPolicy.Invalid();
        return new(StringComparer.Ordinal)
        {
            ["label_id"] = RegistryPolicy.RequiredId(value, "label_id"),
            ["name"] = RegistryPolicy.Text(value.GetProperty("name"), 80, false),
            ["icon"] = value.TryGetProperty("icon", out var icon) ? RegistryPolicy.Icon(icon) : null,
            ["color"] = value.TryGetProperty("color", out var color) ? Color(color) : null,
            ["description"] = value.TryGetProperty("description", out var description) ? RegistryPolicy.Text(description, 1024) : null
        };
    }

    public static void RequireAbsent(JsonElement catalog, string name)
    {
        if (catalog.EnumerateArray().Any(row => NameKey(row.GetProperty("name").GetString()!) == NameKey(name))) throw RegistryPolicy.Invalid();
    }

    public static JsonElement Result(JsonElement value, JsonElement intent, JsonElement before)
    {
        if (new[] { "label_id", "name", "icon", "color", "description", "created_at", "modified_at" }.Any(field => !value.TryGetProperty(field, out _))) throw RegistryPolicy.Invalid();
        var row = Row(value);
        if (before.EnumerateArray().Any(item => item.GetProperty("label_id").GetString() == (string)row["label_id"]!) ||
            row["description"] is not null || new[] { "name", "icon", "color" }.Any(field => JsonSerializer.Serialize(row[field]) != intent.GetProperty(field).GetRawText())) throw RegistryPolicy.Invalid();
        return JsonSerializer.SerializeToElement(row);
    }

    public static string Placeholder(int position) => "gate_pending_" + position.ToString(CultureInfo.InvariantCulture);

    public static string Display(ApprovalPresentation display, string token)
    {
        if (!display.Changes.TryGetValue("new_label", out var name) || name.Before != "Nem létezik" ||
            name.After != display.TargetName || display.Changes.Keys.Any(key => key is not ("new_label" or "icon" or "color"))) throw ApprovalPresentation.CannotDisplay();
        var line = "Új címke: " + ApprovalPresentation.RequireName(name.After, token) + " · nem létezik → létrehozás";
        foreach (var field in new[] { "icon", "color" })
        {
            if (!display.Changes.TryGetValue(field, out var change)) continue;
            if (change.Before != "Nincs" || token.Length > 0 && change.After.Contains(token, StringComparison.Ordinal)) throw ApprovalPresentation.CannotDisplay();
            if (field == "icon") _ = RegistryPolicy.Icon(JsonSerializer.SerializeToElement(change.After));
            else _ = Color(JsonSerializer.SerializeToElement(change.After));
            line += "; " + (field == "icon" ? "ikon" : "szín") + ": " + change.After;
        }
        return line;
    }

    public static int[] References(JsonElement patch)
    {
        RegistrySocket.ValidateUnique(patch);
        if (!patch.TryGetProperty("labels_add_created", out var refs)) return [];
        if (refs.ValueKind != JsonValueKind.Array || refs.GetArrayLength() is < 1 or > 16) throw RegistryPolicy.Invalid();
        var indices = refs.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index) && index is >= 0 and < 50 ? index : throw RegistryPolicy.Invalid()).ToArray();
        if (indices.Distinct().Count() != indices.Length) throw RegistryPolicy.Invalid();
        return indices;
    }

    public static JsonElement Patch(JsonElement source, bool device)
    {
        var refs = References(source);
        var patch = source.EnumerateObject().Where(p => p.Name != "labels_add_created").ToDictionary(p => p.Name, p => p.Value.Clone());
        if (refs.Length > 0)
        {
            var field = device ? "labels" : "labels_add";
            var existing = patch.TryGetValue(field, out var value) ? RegistryPolicy.Ids(value) : [];
            if (existing.Any(id => refs.Any(index => id == Placeholder(index)))) throw RegistryPolicy.Invalid();
            patch[field] = JsonSerializer.SerializeToElement(existing.Concat(refs.Select(Placeholder)).ToArray());
        }
        return JsonSerializer.SerializeToElement(patch);
    }
}
