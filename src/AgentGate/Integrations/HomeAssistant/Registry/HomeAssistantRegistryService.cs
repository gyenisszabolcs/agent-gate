using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Policy;

namespace AgentGate.Integrations.HomeAssistant.Registry;

public sealed class HomeAssistantRegistryService(HomeAssistantClient? configuredClient, RegistryPolicy policy, IKillSwitchStateSource killSwitch)
{
    private HomeAssistantClient client => configuredClient ?? throw RegistryPolicy.Invalid();
    public async Task<PreparedOperation> PrepareAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct)
    {
        ValidateArguments(arguments);
        var entityId = arguments["entity_id"].GetString()!;
        policy.RequireEntity(client, entityId);
        var patch = RegistryPolicy.Patch(arguments["patch"]);
        var intent = JsonSerializer.SerializeToElement(new SortedDictionary<string, object?>(patch, StringComparer.Ordinal));
        var before = Snapshot(await client.ReadRegistryEntityAsync(entityId, intent, ct), entityId);
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
        if (changes.Count == 0 || JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after)) throw RegistryPolicy.Invalid();
        if (patch.TryGetValue("area_id", out var area) && area is string areaId)
            RequireCatalog(await client.ReadRegistryAreasAsync(ct), "area_id", [areaId]);
        if (add.Length + remove.Length > 0 || changes.ContainsKey("labels"))
            RequireCatalog(await client.ReadRegistryLabelsAsync(true, ct), "label_id", add.Concat(remove).Concat((string[])after["labels"]!));
        ct.ThrowIfCancellationRequested();
        return new PreparedOperation(HomeAssistantRegistryOperation.ToolName, RiskLevel.Configuration,
            new Dictionary<string, string?>
            {
                ["entity_id"] = entityId, ["patch"] = intent.GetRawText(), ["before"] = JsonSerializer.Serialize(before),
                ["after"] = JsonSerializer.Serialize(after), ["changes"] = JsonSerializer.Serialize(changes)
            }, requiresLosslessAudit: true);
    }

    public async Task ExecuteAsync(PreparedOperation operation, CancellationToken ct)
    {
        if (operation.Tool != HomeAssistantRegistryOperation.ToolName || operation.Risk != RiskLevel.Configuration) throw RegistryPolicy.Invalid();
        var entityId = operation.Parameters["entity_id"]!;
        using var patch = JsonDocument.Parse(operation.Parameters["patch"]!);
        var arguments = new Dictionary<string, JsonElement> { ["entity_id"] = JsonSerializer.SerializeToElement(entityId), ["patch"] = patch.RootElement.Clone() };
        var current = await PrepareAsync(arguments, ct);
        if (current.ComputeHash("") != operation.ComputeHash("")) throw RegistryPolicy.Invalid();
        using var changes = JsonDocument.Parse(operation.Parameters["changes"]!);
        await client.UpdateRegistryEntityAsync(entityId, changes.RootElement, async token =>
        {
            // Az írási kapcsolat hitelesítése UTÁN, a parancs kiküldése előtt újraolvasunk.
            // Ez szándékosan best-effort; a standard HA update nem nyújt atomi CAS-t.
            var immediate = Snapshot(await client.ReadRegistryEntityAsync(entityId, patch.RootElement, token), entityId);
            if (JsonSerializer.Serialize(immediate) != operation.Parameters["before"]) throw RegistryPolicy.Invalid();
            policy.RequireEntity(client, entityId);
            if (await killSwitch.IsEnabledAsync(token)) throw RegistryPolicy.Invalid();
            token.ThrowIfCancellationRequested();
        }, ct);
    }

    internal static void ValidateArguments(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        // Egyetlen eszközcél sem bizonyítottan teljes a jelenlegi adapterrel; nincs látszatengedély.
        if (arguments.Keys.Any(key => key is not ("entity_id" or "patch")) || arguments.Count != 2 ||
            !arguments.TryGetValue("entity_id", out var entity) || entity.ValueKind != JsonValueKind.String ||
            !arguments.TryGetValue("patch", out var patch)) throw RegistryPolicy.Invalid();
        RegistryPolicy.Patch(patch);
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
    public async Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken)
    {
        await service.ExecuteAsync(operation, cancellationToken);
        return "A Home Assistant nyilvántartási módosítás elkészült.";
    }
}
