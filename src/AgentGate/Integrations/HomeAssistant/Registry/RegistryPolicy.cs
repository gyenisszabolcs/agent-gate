using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentGate.Secrets;
using AgentGate.Tools;
using Microsoft.Extensions.Options;

namespace AgentGate.Integrations.HomeAssistant.Registry;

public sealed class RegistryPolicy
{
    private readonly HashSet<string> denied;
    public bool Enabled { get; }
    public bool IncludeEntityLabels { get; }
    private RegistryPolicy(bool enabled, bool includeEntityLabels, IEnumerable<string> denied) { Enabled = enabled; IncludeEntityLabels = includeEntityLabels; this.denied = denied.ToHashSet(StringComparer.Ordinal); }
    public static RegistryPolicy FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("HomeAssistantRegistry");
        var enabled = false;
        if (section.Value is not null || section.GetChildren().Any(c => c.Key is not ("Enabled" or "EntityDenylist" or "IncludeEntityLabels"))) throw BadConfiguration();
        var flag = section.GetSection("Enabled");
        if (flag.GetChildren().Any() || flag.Value is not null && !bool.TryParse(flag.Value, out enabled)) throw BadConfiguration();
        var includeLabels = false;
        var labelsFlag = section.GetSection("IncludeEntityLabels");
        if (labelsFlag.GetChildren().Any() || labelsFlag.Value is not null && !bool.TryParse(labelsFlag.Value, out includeLabels)) throw BadConfiguration();
        var denied = ReadList(section.GetSection("EntityDenylist"));
        // A már meglévő globális tiltás nem kerülhető meg az új eszközzel.
        denied = denied.Concat(ReadList(configuration.GetSection("HomeAssistantControl:EntityDenylist"))).ToArray();
        if (denied.Any(id => !Regex.IsMatch(id, @"\A[a-z][a-z0-9_]*\.[a-z0-9_]+\z", RegexOptions.CultureInvariant))) throw BadConfiguration();
        return new(enabled, includeLabels, denied);
    }
    private static string[] ReadList(IConfigurationSection section)
    {
        var children = section.GetChildren().ToArray();
        if (section.Value is not null && (section.Value.Length != 0 || children.Length != 0) || children.Where((c, i) => c.Key != i.ToString(CultureInfo.InvariantCulture) || c.Value is null || c.GetChildren().Any()).Any()) throw BadConfiguration();
        var values = children.Select(c => c.Value!).ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw BadConfiguration();
        return values;
    }
    private static OptionsValidationException BadConfiguration() => new(Options.DefaultName, typeof(RegistryPolicy), ["A Home Assistant nyilvántartás konfigurációja érvénytelen."]);
    public void RequireEntity(HomeAssistantClient client, string entityId)
    {
        if (!Enabled || denied.Contains(entityId)) throw Denied();
        try { client.RequireRegistryEntity(entityId); }
        catch (HomeAssistantException) { throw Denied(); }
    }
    internal void RequireDevice()
    {
        if (!Enabled) throw Denied();
    }

    internal static Dictionary<string, object?> DevicePatch(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw Invalid();
        try { RegistrySocket.ValidateUnique(patch); }
        catch (InvalidOperationException) { throw Invalid(); }
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in patch.EnumerateObject())
            result.Add(field.Name, field.Name switch
            {
                "area_id" => field.Value.ValueKind == JsonValueKind.Null ? null : Id(field.Value),
                "name_by_user" => Text(field.Value, 80),
                "labels" => Ids(field.Value),
                _ => throw Invalid()
            });
        if (result.Count == 0) throw Invalid();
        return result;
    }

    internal static GateToolException Denied() => new("A nyilvántartási entitás nem engedélyezett.");
    internal static GateToolException Invalid() => new("A nyilvántartási mező vagy bemenet nem engedélyezett vagy érvénytelen.");
    internal static string? Text(JsonElement element, int maximum, bool nullable = true)
    {
        if (nullable && element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw Invalid();
        var text = element.GetString()!;
        if (text.Length is 0 || text.Length > maximum || text.Trim() != text ||
            text.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format || c is '<' or '>' or '&') ||
            SecretRedactor.RedactText(text) != text) throw Invalid();
        return text;
    }
    internal static string Id(JsonElement element) => Text(element, 64, false) is { } id && Regex.IsMatch(id, @"\A[a-z0-9_-]+\z", RegexOptions.CultureInvariant) ? id : throw Invalid();
    internal static string RequiredId(JsonElement element, string key) => Id(element.GetProperty(key));
    internal static JsonElement[] List(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 4096) throw Invalid();
        return value.EnumerateArray().ToArray();
    }
    internal static string[] Ids(JsonElement value)
    {
        var ids = List(value).Select(Id).ToArray();
        if (ids.Length > 16 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw Invalid();
        return ids.Order(StringComparer.Ordinal).ToArray();
    }

    internal static Dictionary<string, object?> Patch(JsonElement value)
    {
        RegistrySocket.ValidateUnique(value);
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Any()) throw Invalid();
        var patch = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            object? validated = field.Name switch
            {
                "area_id" => field.Value.ValueKind == JsonValueKind.Null ? null : Id(field.Value),
                "labels_add" or "labels_remove" => Ids(field.Value),
                "name" => Text(field.Value, 80),
                "icon" => Icon(field.Value),
                "hidden_by" or "disabled_by" => UserOrNull(field.Value),
                _ => throw Invalid()
            };
            patch.Add(field.Name, validated);
        }
        var add = patch.GetValueOrDefault("labels_add") as string[] ?? [];
        var remove = patch.GetValueOrDefault("labels_remove") as string[] ?? [];
        if (add.Intersect(remove, StringComparer.Ordinal).Any()) throw Invalid();
        return patch;
    }
    internal static string? Icon(JsonElement value)
    {
        var icon = Text(value, 64);
        if (icon is not null && !Regex.IsMatch(icon, @"\Amdi:[a-z0-9-]+\z", RegexOptions.CultureInvariant)) throw Invalid();
        return icon;
    }
    private static string? UserOrNull(JsonElement value)
    {
        var result = Text(value, 4);
        if (result is not (null or "user")) throw Invalid();
        return result;
    }
}
