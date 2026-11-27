using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.Registry;

namespace AgentGate.Integrations.HomeAssistant;

public sealed partial class HomeAssistantClient
{
    private static readonly HashSet<string> RegistryEntityFields = new([
        "entity_id", "area_id", "labels", "name", "icon", "hidden_by", "disabled_by", "device_id",
        "aliases", "capabilities", "categories", "config_entry_id", "config_subentry_id", "created_at",
        "device_class", "display_precision", "entity_category", "has_entity_name", "id", "modified_at",
        "options", "original_device_class", "original_icon", "original_name", "original_name_unprefixed",
        "platform", "translation_key", "unique_id", "previous_unique_id", "next_name_part"
    ], StringComparer.Ordinal);

    internal void RequireRegistryEntity(string entityId)
    {
        if (!IsAllowed(entityId)) throw new HomeAssistantException("A Home Assistant entitás nem engedélyezett.");
    }

    internal async Task<JsonElement> ReadRegistryEntityAsync(string entityId, JsonElement intent, CancellationToken ct)
    {
        RequireRegistryEntity(entityId);
        var result = await RegistrySocket.CallAsync(logSockets, http.BaseAddress!, secrets, secretId, timeout,
            new Dictionary<string, object?> { ["id"] = 1, ["type"] = "config/entity_registry/get", ["entity_id"] = entityId }, intent, true, ct);
        if (result.ValueKind != JsonValueKind.Object || result.EnumerateObject().Any(property => !RegistryEntityFields.Contains(property.Name))) throw RegistryPolicy.Invalid();
        return result;
    }

    internal Task<JsonElement> ReadRegistryAreasAsync(CancellationToken ct) => RegistrySocket.CallAsync(logSockets, http.BaseAddress!, secrets, secretId, timeout,
        new Dictionary<string, object?> { ["id"] = 1, ["type"] = "config/area_registry/list" }, null, true, ct);

    internal async Task<string?> ReadRegistryDeviceAreaAsync(string deviceId, CancellationToken ct)
    {
        var source = await RegistrySocket.CallAsync(logSockets, http.BaseAddress!, secrets, secretId, timeout,
            new Dictionary<string, object?> { ["id"] = 1, ["type"] = "config/device_registry/list" }, null, true, ct);
        // Csak az azonosító és a terület vetülete kell; a többi eszközadat nem jut tovább.
        var areas = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in RegistryPolicy.List(source))
        {
            var id = RegistryPolicy.RequiredId(item, "id");
            var area = item.GetProperty("area_id");
            if (!areas.TryAdd(id, area.ValueKind == JsonValueKind.Null ? null : RegistryPolicy.Id(area))) throw RegistryPolicy.Invalid();
        }
        return areas.TryGetValue(deviceId, out var result) ? result : throw RegistryPolicy.Invalid();
    }

    internal Task<JsonElement> ReadRegistryLabelsAsync(bool lossless, CancellationToken ct) => RegistrySocket.CallAsync(logSockets, http.BaseAddress!, secrets, secretId, timeout,
        new Dictionary<string, object?> { ["id"] = 1, ["type"] = "config/label_registry/list" }, null, lossless, ct);

    internal Task<JsonElement> CreateRegistryLabelAsync(JsonElement intent, Func<CancellationToken, Task> beforeSend, CancellationToken ct)
    {
        var command = new Dictionary<string, object?> { ["id"] = 1, ["type"] = "config/label_registry/create" };
        foreach (var field in intent.EnumerateObject()) command.Add(field.Name, field.Value.Clone());
        return RegistrySocket.CallAsync(logSockets, http.BaseAddress!, secrets, secretId, timeout, command, intent, true, ct, beforeSend);
    }

    internal async Task UpdateRegistryEntityAsync(string entityId, JsonElement changes, Func<CancellationToken, Task> beforeSend, CancellationToken ct)
    {
        RequireRegistryEntity(entityId);
        var command = new Dictionary<string, object?> { ["id"] = 1, ["type"] = "config/entity_registry/update", ["entity_id"] = entityId };
        foreach (var field in changes.EnumerateObject()) command.Add(field.Name, field.Value.Clone());
        await RegistrySocket.CallAsync(logSockets, http.BaseAddress!, secrets, secretId, timeout, command, changes, true, ct, beforeSend);
    }

    public async Task<JsonElement> GetLabelsAsync(CancellationToken cancellationToken = default)
    {
        var source = await ReadRegistryLabelsAsync(false, cancellationToken);
        var items = RegistryPolicy.List(source).Select(item => new
        {
            label_id = RegistryPolicy.RequiredId(item, "label_id"),
            name = RegistryPolicy.Text(item.GetProperty("name"), 80, false)
        }).OrderBy(item => item.label_id, StringComparer.Ordinal).ToArray();
        if (items.Select(item => item.label_id).Distinct(StringComparer.Ordinal).Count() != items.Length) throw RegistryPolicy.Invalid();
        items = items.Where(item => HomeAssistantReadProjection.IsSafeLabel(item.label_id, item.name!)).ToArray();
        return JsonSerializer.SerializeToElement(new { items = items.Take(100), returned_count = Math.Min(items.Length, 100), truncated = items.Length > 100 });
    }

    public async Task<string[]> GetEntityLabelsAsync(string entityId, CancellationToken cancellationToken = default)
    {
        using var intent = JsonDocument.Parse("{}");
        var source = await ReadRegistryEntityAsync(entityId, intent.RootElement, cancellationToken);
        if (source.GetProperty("entity_id").GetString() != entityId) throw RegistryPolicy.Invalid();
        return RegistryPolicy.Ids(source.GetProperty("labels")).Where(label => HomeAssistantReadProjection.IsSafeLabel(label, label)).ToArray();
    }
}
