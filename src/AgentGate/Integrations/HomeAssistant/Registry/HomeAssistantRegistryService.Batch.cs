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
            ValidateArguments(member);
            var kind = member.ContainsKey("device_id") ? "device_id" : "entity_id";
            if (!targets.Add(kind + ":" + member[kind].GetString())) throw RegistryPolicy.Invalid();
            result.Add(member);
        }
        return result;
    }

    private async Task<PreparedOperation> PrepareBatchAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken ct)
    {
        var members = new List<PreparedOperation>();
        foreach (var item in BatchArguments(arguments))
        {
            ct.ThrowIfCancellationRequested();
            members.Add(await PrepareCoreAsync(item, ct, true, extended: true));
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
