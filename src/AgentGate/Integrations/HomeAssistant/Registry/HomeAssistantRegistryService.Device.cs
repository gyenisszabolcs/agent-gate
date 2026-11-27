using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Policy;
using AgentGate.Tools;

namespace AgentGate.Integrations.HomeAssistant.Registry;

public sealed partial class HomeAssistantRegistryService
{
    private async Task<PreparedOperation> PrepareDeviceAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct, bool includePresentation, bool extended = false, JsonElement? labelCatalog = null)
    {
        policy.RequireDevice();
        var deviceId = RegistryPolicy.Id(arguments["device_id"]);
        var patch = RegistryPolicy.DevicePatch(arguments["patch"]);
        var before = await ReadDeviceSnapshotAsync(deviceId, ct);
        var after = new SortedDictionary<string, object?>(before, StringComparer.Ordinal);
        var changes = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in patch)
        {
            if (JsonSerializer.Serialize(before[field]) == JsonSerializer.Serialize(value)) continue;
            after[field] = value;
            changes.Add(field, value);
        }
        if (changes.Count == 0) throw RegistryPolicy.Invalid();
        JsonElement? areas = null;
        JsonElement? labels = null;
        var requiredAreas = new[] { before["area_id"], after["area_id"] }.OfType<string>().ToArray();
        if (requiredAreas.Length != 0)
        {
            areas = await client.ReadRegistryAreasAsync(ct);
            RequireCatalog(areas.Value, "area_id", requiredAreas);
        }
        var requiredLabels = ((string[])before["labels"]!).Concat((string[])after["labels"]!).ToArray();
        if (requiredLabels.Length != 0)
        {
            labels = labelCatalog ?? await client.ReadRegistryLabelsAsync(true, ct);
            RequireCatalog(labels.Value, "label_id", requiredLabels);
        }
        ApprovalPresentation? presentation = null;
        if (includePresentation)
        {
            var display = new Dictionary<string, ApprovalDisplayChange>(StringComparer.Ordinal);
            foreach (var field in changes.Keys)
            {
                var oldValue = DeviceDisplayValue(field, before[field], areas, labels);
                var newValue = DeviceDisplayValue(field, after[field], areas, labels);
                if (CanonicalDisplay(oldValue) == CanonicalDisplay(newValue)) throw ApprovalPresentation.CannotDisplay();
                display.Add(field, new(oldValue, newValue));
            }
            presentation = new(before["name_by_user"] as string ?? before["name"] as string, display);
            _ = presentation.RegistryLine(extended: extended);
        }
        ct.ThrowIfCancellationRequested();
        return new(HomeAssistantRegistryOperation.ToolName, RiskLevel.Configuration, new Dictionary<string, string?>
        {
            ["device_id"] = deviceId,
            ["patch"] = JsonSerializer.Serialize(new SortedDictionary<string, object?>(patch, StringComparer.Ordinal)),
            ["before"] = JsonSerializer.Serialize(before), ["after"] = JsonSerializer.Serialize(after), ["changes"] = JsonSerializer.Serialize(changes)
        }, requiresLosslessAudit: true, presentation: presentation);
    }

    private async Task<SortedDictionary<string, object?>> ReadDeviceSnapshotAsync(string deviceId, CancellationToken ct)
    {
        policy.RequireDevice();
        var device = await client.ReadRegistryDeviceAsync(deviceId, ct);
        var entities = await client.ReadRegistryActiveDeviceEntitiesAsync(deviceId, ct);
        // Nem Any: minden aktív entitásnak meg kell felelnie mindkét policy-nak.
        foreach (var entity in entities) policy.RequireEntity(client, entity);
        return new(StringComparer.Ordinal)
        {
            ["area_id"] = NullableId(device.GetProperty("area_id")),
            ["labels"] = RegistryPolicy.Ids(device.GetProperty("labels")),
            ["name_by_user"] = RegistryPolicy.Text(device.GetProperty("name_by_user"), 80),
            ["name"] = RegistryPolicy.Text(device.GetProperty("name"), 80),
            ["parent_device_id"] = NullableId(device.GetProperty("parent_device_id")),
            ["disabled_by"] = Owner(device.GetProperty("disabled_by")),
            ["active_entities"] = entities
        };
    }

    private static string DeviceDisplayValue(string field, object? value, JsonElement? areas, JsonElement? labels) => field switch
    {
        "area_id" => value is string id ? CatalogName(areas, "area_id", id) : "Nincs",
        "name_by_user" => DisplayValue("name", value, labels),
        "labels" => DisplayValue(field, value, labels),
        _ => throw RegistryPolicy.Invalid()
    };

    private async Task ExecuteDeviceAsync(PreparedOperation operation, CancellationToken ct, Func<CancellationToken, Task>? checkCatalog = null)
    {
        if (operation.Parameters.Count != 5 || operation.Parameters.ContainsKey("entity_id")) throw RegistryPolicy.Invalid();
        var arguments = new Dictionary<string, JsonElement>
        {
            ["device_id"] = JsonSerializer.SerializeToElement(operation.Parameters["device_id"]),
            ["patch"] = JsonSerializer.Deserialize<JsonElement>(operation.Parameters["patch"]!)
        };
        async Task Revalidate(CancellationToken token)
        {
            PreparedOperation current;
            try { current = await PrepareCoreAsync(arguments, token, false); }
            catch (GateToolException) { throw new RegistryPrestateChangedException(); }
            if (current.ComputeHash("") != operation.ComputeHash("")) throw new RegistryPrestateChangedException();
        }
        await Revalidate(ct);
        using var changes = JsonDocument.Parse(operation.Parameters["changes"]!);
        await client.UpdateRegistryDeviceAsync(operation.Parameters["device_id"]!, changes.RootElement, async token =>
        {
            // Az írási socket hitelesítése után is teljes policy/catalog/előállapot-ellenőrzés.
            await Revalidate(token);
            if (checkCatalog is not null) await checkCatalog(token);
            if (await killSwitch.IsEnabledAsync(token)) throw RegistryPolicy.Invalid();
            token.ThrowIfCancellationRequested();
        }, ct);
    }
}
