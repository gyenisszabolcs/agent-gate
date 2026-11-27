using System.Text;
using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Policy;
using AgentGate.Tools;

namespace AgentGate.Integrations.HomeAssistant.Registry;

public sealed partial class HomeAssistantRegistryService(HomeAssistantClient? configuredClient, RegistryPolicy policy, IKillSwitchStateSource killSwitch)
{
    private HomeAssistantClient client => configuredClient ?? throw RegistryPolicy.Invalid();
    public Task<PreparedOperation> PrepareAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct) =>
        arguments.ContainsKey("items") ? PrepareBatchAsync(arguments, ct) : PrepareCoreAsync(arguments, ct, true);

    private async Task<PreparedOperation> PrepareCoreAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct, bool includePresentation, bool extended = false)
    {
        ValidateArguments(arguments);
        if (arguments.ContainsKey("device_id")) return await PrepareDeviceAsync(arguments, ct, includePresentation, extended);
        var entityId = arguments["entity_id"].GetString()!;
        policy.RequireEntity(client, entityId);
        var patch = RegistryPolicy.Patch(arguments["patch"]);
        var intent = JsonSerializer.SerializeToElement(new SortedDictionary<string, object?>(patch, StringComparer.Ordinal));
        var before = await ReadSnapshotAsync(entityId, intent, ct);
        var after = new SortedDictionary<string, object?>(before, StringComparer.Ordinal);
        var changes = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in patch.Where(pair => pair.Key is not ("labels_add" or "labels_remove")))
        {
            if (name is "hidden_by" or "disabled_by" && before[name] is not (null or "user")) throw RegistryPolicy.Invalid();
            after[name] = value;
            changes[name] = value;
        }
        var add = patch.GetValueOrDefault("labels_add") as string[] ?? [];
        var remove = patch.GetValueOrDefault("labels_remove") as string[] ?? [];
        if (patch.ContainsKey("labels_add") || patch.ContainsKey("labels_remove"))
        {
            var labels = ((string[])before["labels"]!).Except(remove, StringComparer.Ordinal).Union(add, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (labels.Length > 16) throw RegistryPolicy.Invalid();
            after["labels"] = labels;
            changes["labels"] = labels;
        }
        ResolveArea(after);
        if (changes.Count == 0 || JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after)) throw RegistryPolicy.Invalid();
        JsonElement? areas = null;
        JsonElement? labelsCatalog = null;
        if (patch.TryGetValue("area_id", out var area) && area is string areaId)
        {
            areas = await client.ReadRegistryAreasAsync(ct);
            RequireCatalog(areas.Value, "area_id", [areaId]);
        }
        if (add.Length + remove.Length > 0 || changes.ContainsKey("labels"))
        {
            labelsCatalog = await client.ReadRegistryLabelsAsync(true, ct);
            RequireCatalog(labelsCatalog.Value, "label_id", add.Concat(remove).Concat((string[])after["labels"]!));
        }
        ApprovalPresentation? presentation = null;
        if (includePresentation)
        {
            // Ugyanazok a szűk registry-olvasások, az előállapot feloldására is. Nincs általános végrehajtó.
            if (changes.ContainsKey("area_id") && areas is null && (before["effective_area_id"] is not null || after["effective_area_id"] is not null))
                areas = await client.ReadRegistryAreasAsync(ct);
            var display = new Dictionary<string, ApprovalDisplayChange>(StringComparer.Ordinal);
            foreach (var field in changes.Keys)
            {
                var oldValue = field == "area_id" ? DisplayArea(before, areas) : DisplayValue(field, before[field], labelsCatalog);
                var newValue = field == "area_id" ? DisplayArea(after, areas) : DisplayValue(field, after[field], labelsCatalog);
                if (CanonicalDisplay(oldValue) == CanonicalDisplay(newValue) && JsonSerializer.Serialize(before[field]) != JsonSerializer.Serialize(after[field]))
                    throw ApprovalPresentation.CannotDisplay();
                display.Add(field, new(oldValue, newValue));
            }
            presentation = new(before["name"] as string, display);
            _ = presentation.RegistryLine(extended: extended);
        }
        ct.ThrowIfCancellationRequested();
        return new PreparedOperation(HomeAssistantRegistryOperation.ToolName, RiskLevel.Configuration,
            new Dictionary<string, string?>
            {
                ["entity_id"] = entityId, ["patch"] = intent.GetRawText(), ["before"] = JsonSerializer.Serialize(before),
                ["after"] = JsonSerializer.Serialize(after), ["changes"] = JsonSerializer.Serialize(changes)
            }, requiresLosslessAudit: true, presentation: presentation);
    }

    public async Task ExecuteAsync(PreparedOperation operation, CancellationToken ct)
    {
        if (operation.Tool != HomeAssistantRegistryOperation.ToolName || operation.Risk != RiskLevel.Configuration) throw RegistryPolicy.Invalid();
        if (operation.Parameters.ContainsKey("device_id"))
        {
            await ExecuteDeviceAsync(operation, ct);
            return;
        }
        var entityId = operation.Parameters["entity_id"]!;
        using var patch = JsonDocument.Parse(operation.Parameters["patch"]!);
        var arguments = new Dictionary<string, JsonElement> { ["entity_id"] = JsonSerializer.SerializeToElement(entityId), ["patch"] = patch.RootElement.Clone() };
        // A megjelenő név nem célegyezés: a végrehajtás változatlanul a kötött paramétereket ellenőrzi.
        PreparedOperation current;
        try { current = await PrepareCoreAsync(arguments, ct, false); }
        catch (GateToolException) { throw new RegistryPrestateChangedException(); }
        if (current.ComputeHash("") != operation.ComputeHash("")) throw new RegistryPrestateChangedException();
        using var changes = JsonDocument.Parse(operation.Parameters["changes"]!);
        await client.UpdateRegistryEntityAsync(entityId, changes.RootElement, async token =>
        {
            // Az írási kapcsolat hitelesítése UTÁN, a parancs kiküldése előtt újraolvasunk.
            // Ez szándékosan best-effort; a standard HA update nem nyújt atomi CAS-t.
            var immediate = await ReadSnapshotAsync(entityId, patch.RootElement, token);
            if (JsonSerializer.Serialize(immediate) != operation.Parameters["before"]) throw new RegistryPrestateChangedException();
            policy.RequireEntity(client, entityId);
            if (await killSwitch.IsEnabledAsync(token)) throw RegistryPolicy.Invalid();
            token.ThrowIfCancellationRequested();
        }, ct);
    }

    internal static void ValidateArguments(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (arguments.ContainsKey("items"))
        {
            _ = BatchArguments(arguments);
            return;
        }
        if (arguments.Keys.Any(key => key is not ("entity_id" or "device_id" or "patch")) || arguments.Count != 2 ||
            !arguments.TryGetValue("patch", out var patch)) throw RegistryPolicy.Invalid();
        if (arguments.TryGetValue("device_id", out var device))
        {
            _ = RegistryPolicy.Id(device);
            _ = RegistryPolicy.DevicePatch(patch);
        }
        else
        {
            if (!arguments.TryGetValue("entity_id", out var entity) || entity.ValueKind != JsonValueKind.String) throw RegistryPolicy.Invalid();
            _ = RegistryPolicy.Patch(patch);
        }
    }

    private async Task<SortedDictionary<string, object?>> ReadSnapshotAsync(string entityId, JsonElement intent, CancellationToken ct)
    {
        var snapshot = Snapshot(await client.ReadRegistryEntityAsync(entityId, intent, ct), entityId);
        snapshot["device_area_id"] = snapshot["device_id"] is string deviceId ? await client.ReadRegistryDeviceAreaAsync(deviceId, ct) : null;
        ResolveArea(snapshot);
        return snapshot;
    }

    private static void ResolveArea(IDictionary<string, object?> snapshot)
    {
        snapshot["effective_area_id"] = snapshot["area_id"] ?? snapshot["device_area_id"];
        snapshot["area_source"] = snapshot["area_id"] is not null ? "entity" : snapshot["device_area_id"] is not null ? "device" : "none";
    }

    private static SortedDictionary<string, object?> Snapshot(JsonElement entity, string entityId)
    {
        RegistrySocket.ValidateUnique(entity);
        if (entity.GetProperty("entity_id").GetString() != entityId) throw RegistryPolicy.Invalid();
        return new(StringComparer.Ordinal)
        {
            ["device_id"] = NullableId(entity.GetProperty("device_id")),
            ["area_id"] = NullableId(entity.GetProperty("area_id")),
            ["labels"] = RegistryPolicy.Ids(entity.GetProperty("labels")),
            ["name"] = RegistryPolicy.Text(entity.GetProperty("name"), 80),
            ["icon"] = RegistryPolicy.Icon(entity.GetProperty("icon")),
            ["hidden_by"] = Owner(entity.GetProperty("hidden_by")),
            ["disabled_by"] = Owner(entity.GetProperty("disabled_by"))
        };
    }
    private static string? NullableId(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : RegistryPolicy.Id(value);
    private static string? Owner(JsonElement value)
    {
        var owner = RegistryPolicy.Text(value, 32);
        if (owner is not (null or "user" or "integration" or "config_entry" or "device")) throw RegistryPolicy.Invalid();
        return owner;
    }
    private static string DisplayValue(string field, object? value, JsonElement? labels)
    {
        return field switch
        {
            "name" => value is null ? "Alapértelmezett" : DisplayName((string)value, "Alapértelmezett"),
            "labels" => ((string[])value!).Length == 0 ? "Nincs" : string.Join(", ", ((string[])value!).Select(id => CatalogName(labels, "label_id", id)).Order(StringComparer.Ordinal)),
            "hidden_by" or "disabled_by" => value switch { null => "Nem", "user" => "Igen", _ => throw ApprovalPresentation.CannotDisplay() },
            _ => throw ApprovalPresentation.CannotDisplay()
        };
    }

    private static string DisplayArea(IDictionary<string, object?> snapshot, JsonElement? areas) =>
        snapshot["effective_area_id"] is not string id ? "Nincs" : CatalogName(areas, "area_id", id) +
            (snapshot["area_source"] is "device" ? " (öröklött)" : " (saját)");

    private static string CatalogName(JsonElement? source, string key, string id)
    {
        if (source is null) throw ApprovalPresentation.CannotDisplay();
        var items = RegistryPolicy.List(source.Value).ToArray();
        var matches = items.Where(item => RegistryPolicy.RequiredId(item, key) == id).ToArray();
        if (matches.Length != 1 || !matches[0].TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            throw ApprovalPresentation.CannotDisplay();
        var text = DisplayName(name.GetString(), "Nincs");
        if (text == id || items.Count(item => item.TryGetProperty("name", out var other) && other.ValueKind == JsonValueKind.String && CanonicalDisplay(other.GetString()!) == text) != 1)
            throw ApprovalPresentation.CannotDisplay();
        return text;
    }

    private static string DisplayName(string? value, string sentinel)
    {
        var text = CanonicalDisplay(ApprovalPresentation.RequireName(value));
        if (text == sentinel) throw ApprovalPresentation.CannotDisplay();
        return text;
    }

    // Kizárólag megjelenítési összevetés és rendezés; a kötött értékekhez nem nyúlunk.
    private static string CanonicalDisplay(string value)
    {
        try { return value.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw ApprovalPresentation.CannotDisplay(); }
    }

    private static void RequireCatalog(JsonElement source, string key, IEnumerable<string> required)
    {
        var ids = RegistryPolicy.List(source).Select(item => RegistryPolicy.RequiredId(item, key)).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || required.Any(id => !ids.Contains(id, StringComparer.Ordinal))) throw RegistryPolicy.Invalid();
    }
}

public sealed class HomeAssistantRegistryOperation(HomeAssistantRegistryService service) : IApprovalOperationHandler
{
    public const string ToolName = "ha_update_registry";
    public string Tool => ToolName;
    public RiskLevel Risk => RiskLevel.Configuration;
    public ValueTask<PreparedOperation> PrepareAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken) => new(service.PrepareAsync(arguments, cancellationToken));
    public Task<string> ExecuteAsync(PreparedOperation operation, ApprovalExecutionContext context, CancellationToken cancellationToken) =>
        operation.Parameters.ContainsKey("items") ? service.ExecuteBatchAsync(operation, context, cancellationToken) : ExecuteAsync(operation, cancellationToken);
    public async Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken)
    {
        await service.ExecuteAsync(operation, cancellationToken);
        return "A Home Assistant nyilvántartási módosítás elkészült.";
    }
}
