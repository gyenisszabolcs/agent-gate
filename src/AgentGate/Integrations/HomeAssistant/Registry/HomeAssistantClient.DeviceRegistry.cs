using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.Registry;

namespace AgentGate.Integrations.HomeAssistant;

public sealed partial class HomeAssistantClient
{
    // HA Core 2026.11.0: DeviceEntry.dict_repr és ChildDeviceEntry.dict_repr.
    private static readonly HashSet<string> RegistryDeviceFields = new([
        "area_id", "configuration_url", "config_entries", "config_entries_subentries", "config_entry_id",
        "config_subentry_id", "connections", "created_at", "disabled_by", "entry_type", "hw_version", "id",
        "identifiers", "labels", "manufacturer", "model", "model_id", "modified_at", "name_by_user", "name",
        "next_name_part", "parent_device_id", "primary_config_entry", "serial_number", "sw_version", "via_device_id"
    ], StringComparer.Ordinal);

    internal async Task<JsonElement> ReadRegistryDeviceAsync(string deviceId, CancellationToken ct)
    {
        var source = await RegistrySocket.CallAsync(logSockets, http.BaseAddress!, secrets, secretId, timeout,
            new Dictionary<string, object?> { ["id"] = 1, ["type"] = "config/device_registry/list" }, null, true, ct);
        var devices = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in RegistryPolicy.List(source))
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(field => !RegistryDeviceFields.Contains(field.Name)) ||
                !devices.TryAdd(RegistryPolicy.RequiredId(item, "id"), item)) throw RegistryPolicy.Invalid();
        }
        return devices.TryGetValue(deviceId, out var device) ? device : throw RegistryPolicy.Invalid();
    }

    internal async Task<string[]> ReadRegistryActiveDeviceEntitiesAsync(string deviceId, CancellationToken ct)
    {
        // Kizárólag kódban rögzített sablon; a cél JSON-stringként kerül bele.
        // A 2026.11.0 helper pontosan az aktív (nem letiltott) registry-bejegyzéseket adja.
        var result = await SendAsync("/api/template", ct,
            JsonSerializer.Serialize(new { template = "{{ device_entities(" + JsonSerializer.Serialize(deviceId) + ") | to_json }}" }));
        var entities = RegistryPolicy.List(result).Select(value => RegistryPolicy.Text(value, 256, false)!).ToArray();
        if (entities.Length == 0 || entities.Distinct(StringComparer.Ordinal).Count() != entities.Length) throw RegistryPolicy.Invalid();
        return entities.Order(StringComparer.Ordinal).ToArray();
    }

    internal async Task UpdateRegistryDeviceAsync(string deviceId, JsonElement changes, Func<CancellationToken, Task> beforeSend, CancellationToken ct)
    {
        var command = new Dictionary<string, object?> { ["id"] = 1, ["type"] = "config/device_registry/update", ["device_id"] = deviceId };
        foreach (var field in changes.EnumerateObject()) command.Add(field.Name, field.Value.Clone());
        // A HA hibáját nem követi újrapróbálás, szülőcél vagy más végrehajtási útvonal.
        await RegistrySocket.CallAsync(logSockets, http.BaseAddress!, secrets, secretId, timeout, command, changes, true, ct, beforeSend);
    }
}
