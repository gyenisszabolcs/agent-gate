using System.Text.Json;
using AgentGate.Policy;
using AgentGate.Tools;

namespace AgentGate.Approval;

internal sealed class ApprovalStatusCallPolicy : IToolCallPolicy
{
    public bool Handles(string tool) => tool == "gate_approval_status";
    public void ValidateArguments(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        // Az SDK stringből is tud intet kötni; a nyers JSON-nál tartjuk a publikált szerződést.
        if (arguments.TryGetValue("wait_seconds", out var wait) &&
            (wait.ValueKind != JsonValueKind.Number || !wait.TryGetInt32(out var seconds) || seconds is < 0 or > 120))
            throw new GateToolException("A wait_seconds csak 0 és 120 közötti egész szám lehet.");
    }
    public void EnrichAuditParameters(IReadOnlyDictionary<string, JsonElement> arguments, RiskLevel risk,
        IDictionary<string, string?> parameters) { }
}
