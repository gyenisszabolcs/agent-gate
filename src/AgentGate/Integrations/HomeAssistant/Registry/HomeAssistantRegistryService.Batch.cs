using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Policy;
using AgentGate.Secrets;

namespace AgentGate.Integrations.HomeAssistant.Registry;

public sealed partial class HomeAssistantRegistryService
{
    internal static IReadOnlyList<Dictionary<string, JsonElement>> BatchArguments(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (arguments.Count != 1 || !arguments.TryGetValue("items", out var items) || items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() is < 1 or > 50) throw RegistryPolicy.Invalid();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Dictionary<string, JsonElement>>();
        foreach (var item in items.EnumerateArray())
        {
            RegistrySocket.ValidateUnique(item);
            if (item.ValueKind != JsonValueKind.Object || item.TryGetProperty("items", out _)) throw RegistryPolicy.Invalid();
            var member = item.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone(), StringComparer.Ordinal);
            ValidateArguments(member, batch: true);
            if (!member.ContainsKey("create_label"))
            {
                var kind = member.ContainsKey("device_id") ? "device_id" : "entity_id";
                if (!targets.Add(kind + ":" + member[kind].GetString())) throw RegistryPolicy.Invalid();
                foreach (var index in LabelCreation.References(member["patch"]))
                    if (index >= result.Count || !result[index].ContainsKey("create_label")) throw RegistryPolicy.Invalid();
                foreach (var field in new[] { "labels", "labels_add", "labels_remove" })
                    if (member["patch"].TryGetProperty(field, out var ids) && RegistryPolicy.Ids(ids).Any(id => id.StartsWith("gate_pending_", StringComparison.Ordinal))) throw RegistryPolicy.Invalid();
            }
            result.Add(member);
        }
        return result;
    }

    private async Task<PreparedOperation> PrepareBatchAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct)
    {
        var members = new List<PreparedOperation>();
        var inputs = BatchArguments(arguments);
        JsonElement? catalog = null;
        if (inputs.Any(item => item.ContainsKey("create_label")))
        {
            catalog = LabelCreation.Catalog(await client.ReadRegistryLabelsAsync(true, ct));
            if (catalog.Value.EnumerateArray().Any(row => row.GetProperty("label_id").GetString()!.StartsWith("gate_pending_", StringComparison.Ordinal))) throw RegistryPolicy.Invalid();
        }
        foreach (var item in inputs)
        {
            ct.ThrowIfCancellationRequested();
            var argumentsForMember = new Dictionary<string, JsonElement>(item, StringComparer.Ordinal);
            var refs = item.TryGetValue("patch", out var patch) ? LabelCreation.References(patch) : [];
            if (refs.Length > 0) argumentsForMember["patch"] = LabelCreation.Patch(patch, item.ContainsKey("device_id"));
            var member = await PrepareCoreAsync(argumentsForMember, ct, true, extended: true, labelCatalog: catalog);
            if (catalog is not null && !item.ContainsKey("create_label"))
            {
                var parametersForMember = member.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value);
                parametersForMember["label_catalog"] = catalog.Value.GetRawText();
                parametersForMember["label_refs"] = JsonSerializer.Serialize(refs.Select(index => new LabelBinding(index, members[index].ComputeHash(""), LabelCreation.Placeholder(index))));
                member = new(member.Tool, member.Risk, parametersForMember, requiresLosslessAudit: true, presentation: member.Presentation);
            }
            members.Add(member);
            if (item.TryGetValue("create_label", out var creation))
            {
                var row = LabelCreation.Intent(creation);
                row.Add("label_id", LabelCreation.Placeholder(members.Count - 1));
                row.Add("description", null);
                catalog = LabelCreation.Catalog(JsonSerializer.SerializeToElement(catalog!.Value.EnumerateArray().Append(JsonSerializer.SerializeToElement(row))));
            }
        }
        var parameters = new Dictionary<string, string?> { ["items"] = JsonSerializer.Serialize(members.Select(member => member.Parameters)) };
        // Ugyanaz a veszteségmentes titokszűrés: a nagy csomagot sem csonkítjuk.
        var operation = new PreparedOperation(HomeAssistantRegistryOperation.ToolName, RiskLevel.Configuration, parameters, requiresLosslessAudit: true,
            presentation: new(null, items: members.Select(member => member.Presentation!).ToArray()));
        _ = ApprovalBatchDetails.Items(operation);
        return operation;
    }

    internal static IReadOnlyList<PreparedOperation> BatchMembers(PreparedOperation operation)
    {
        if (operation.Tool != HomeAssistantRegistryOperation.ToolName || operation.Risk != RiskLevel.Configuration || operation.Parameters.Count != 1)
            throw RegistryPolicy.Invalid();
        using var document = JsonDocument.Parse(operation.Parameters["items"]!);
        RegistrySocket.ValidateUnique(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 or > 50) throw RegistryPolicy.Invalid();
        return document.RootElement.EnumerateArray().Select(item => new PreparedOperation(HomeAssistantRegistryOperation.ToolName,
            RiskLevel.Configuration, item.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString()), requiresLosslessAudit: true)).ToArray();
    }
}
