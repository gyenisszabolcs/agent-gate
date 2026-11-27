using System.Text.Json;
using System.Text.RegularExpressions;
using AgentGate.Secrets;

namespace AgentGate.Integrations.HomeAssistant;

public sealed partial class HomeAssistantClient
{
    // HA Core 2026.11.0: közvetlen registry-helperek, nem a veszteséges listázó serializer.
    // A device_id név szerinti fallbackjét az aktív entitáskapcsolat visszaellenőrzése zárja ki.
    private const string StateDevicesTemplate = """
        {% set ns = namespace(items=[]) %}
        {% for entity in entity_ids %}
        {% set id = device_id(entity) %}
        {% if id is none %}
        {% set ns.items = ns.items + [{'entity_id': entity, 'device': none}] %}
        {% elif device_attr(id, 'id') != id or entity not in device_entities(id) %}
        {% set ns.items = ns.items + [{'invalid': true}] %}
        {% else %}
        {% set own_area = device_attr(id, 'area_id') %}
        {% if own_area is not none and own_area not in areas() %}
        {% set ns.items = ns.items + [{'invalid': true}] %}
        {% else %}
        {% set area = none if own_area is none else {'area_id': own_area, 'area_name': area_name(own_area)} %}
        {% set name = device_attr(id, 'name_by_user') or device_attr(id, 'name') %}
        {% set ns.items = ns.items + [{'entity_id': entity, 'device': {'device_id': id, 'name': name, 'area': area}}] %}
        {% endif %}
        {% endif %}
        {% endfor %}
        {{ ns.items | to_json }}
        """;

    internal async Task<IReadOnlyDictionary<string, JsonElement>> GetStateDevicesAsync(
        IEnumerable<string> entityIds, CancellationToken cancellationToken)
    {
        var ids = entityIds.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (ids.Any(id => !IsAllowed(id))) throw InvalidResponse();
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (ids.Count == 0) return result;
        // Csak már engedélyezett, szigorú ASCII entity_id kerül JSON-stringként a rögzített sablonba.
        var template = "{% set entity_ids = " + JsonSerializer.Serialize(ids.Order(StringComparer.Ordinal)) + " %}\n" + StateDevicesTemplate;
        if (template.Length > MaximumResponseBytes) throw InvalidResponse();
        var json = await SendAsync("/api/template", cancellationToken, JsonSerializer.Serialize(new { template }));
        if (json.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        foreach (var row in json.EnumerateArray())
        {
            if (!HasFields(row, "entity_id", "device") || !TryString(row, "entity_id", out var entity) ||
                !ids.Contains(entity) || !result.TryAdd(entity, row.GetProperty("device"))) throw InvalidResponse();
            var device = row.GetProperty("device");
            if (device.ValueKind == JsonValueKind.Null) continue;
            if (!HasFields(device, "device_id", "name", "area") || !TryString(device, "device_id", out var id) ||
                !IsDeviceId(id) || !SafeDeviceText(device.GetProperty("name"), nullable: true)) throw InvalidResponse();
            var area = device.GetProperty("area");
            if (area.ValueKind != JsonValueKind.Null &&
                (!HasFields(area, "area_id", "area_name") || !TryString(area, "area_id", out var areaId) || !IsDeviceId(areaId) ||
                !SafeDeviceText(area.GetProperty("area_name"), nullable: false))) throw InvalidResponse();
            // A strukturális és ismert-token szűrésen túl ugyanaz a szöveges titokszűrés,
            // mint a többi megjelenítési mezőnél; a metaadat nem kerül ki nyersen.
            result[entity] = JsonSerializer.SerializeToElement(new
            {
                device_id = id,
                name = SecretRedactor.RedactText(device.GetProperty("name").GetString()),
                area = area.ValueKind == JsonValueKind.Null ? null : new Dictionary<string, string?>
                {
                    ["area_id"] = area.GetProperty("area_id").GetString(),
                    ["area_name"] = SecretRedactor.RedactText(area.GetProperty("area_name").GetString())
                }
            });
        }
        // Hiányzó sor nem bizonyítja az eszköz hiányát; csak explicit null jelentheti azt.
        if (result.Count != ids.Count) throw InvalidResponse();
        return result;
    }

    internal static bool IsDeviceId(string value) => value.Length is > 0 and <= 64 &&
        Regex.IsMatch(value, @"\A[a-z0-9_-]+\z", RegexOptions.CultureInvariant) &&
        HomeAssistantReadProjection.IsSafeLabel(value, value);

    private static bool SafeDeviceText(JsonElement value, bool nullable) =>
        nullable && value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 and <= 256 } text && !string.IsNullOrWhiteSpace(text) &&
        !text.Any(char.IsControl) && HomeAssistantReadProjection.IsSafeLabel("", text);

    private static bool HasFields(JsonElement value, params string[] names) =>
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(field => field.Name)
            .Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal);
}
