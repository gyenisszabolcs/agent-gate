using System.Collections.Frozen;
using System.Text.Json;
using AgentGate.Secrets;

namespace AgentGate.Approval;

internal static class ApprovalBatchDetails
{
    public static bool IsBatch(PreparedOperation operation) => operation.Tool == "ha_update_registry" && operation.Parameters.ContainsKey("items");

    public static IReadOnlyList<ApprovalBatchItem>? Items(PreparedOperation operation)
    {
        if (!IsBatch(operation)) return null;
        if (operation.Parameters.Count != 1 || operation.Parameters["items"] is not { Length: <= 1_048_576 } json ||
            operation.Presentation?.Items is not { Count: >= 1 and <= 50 } displays) throw ApprovalPresentation.CannotDisplay();
        var members = JsonSerializer.Deserialize<Dictionary<string, string?>[]>(json) ?? throw ApprovalPresentation.CannotDisplay();
        if (members.Length != displays.Count) throw ApprovalPresentation.CannotDisplay();
        var result = new List<ApprovalBatchItem>();
        for (var index = 0; index < members.Length; index++)
        {
            var member = members[index];
            var safe = SecretRedactor.RedactParameters(member);
            if (member.Any(pair => !safe.TryGetValue(pair.Key, out var value) || value != pair.Value || SecretRedactor.RedactText(pair.Value) != pair.Value))
                throw ApprovalPresentation.CannotDisplay();
            var target = member.GetValueOrDefault("entity_id") ?? member.GetValueOrDefault("device_id") ??
                (member.ContainsKey("create_label") ? "új címke" : throw ApprovalPresentation.CannotDisplay());
            result.Add(new(index + 1, target, ApprovalPresentation.RequireName(displays[index].TargetName),
                member.ToFrozenDictionary(StringComparer.Ordinal), displays[index]));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    public static ApprovalOutcome Snapshot(Guid id, ApprovalState state, string? result, PreparedOperation operation, ApprovalBatchProgress? progress)
    {
        var items = Items(operation)?.Select(item => item with
        {
            State = progress is null ? "NotStarted" : item.Position <= progress.Completed ? "Succeeded" :
                item.Position == progress.StoppedAt ? "Failed" : "NotStarted"
        }).ToArray();
        return new(id, state, result, items is null ? null : Array.AsReadOnly(items), progress);
    }
}
