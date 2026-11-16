using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Policy;

namespace AgentGate.Integrations.HomeAssistant.Control;

public sealed class HomeAssistantControlOperation(HomeAssistantControlService control) : IApprovalOperationHandler
{
    public string Tool => "ha_sensitive_control";
    public RiskLevel Risk => RiskLevel.SensitiveControl;
    public async ValueTask<PreparedOperation> PrepareAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        var request = await control.PrepareAsync(arguments, Risk, cancellationToken);
        return new PreparedOperation(Tool, Risk, new Dictionary<string, string?>
        {
            ["domain"] = request.Domain,
            ["service"] = request.Service,
            ["entity_id"] = request.EntityId,
            ["data"] = request.Data.GetRawText()
        });
    }
    public async Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken)
    {
        if (operation.Tool != Tool || operation.Risk != Risk || operation.Parameters.Count != 4) throw ControlPolicy.Denied();
        using var data = JsonDocument.Parse(operation.Parameters["data"]!);
        var arguments = operation.Parameters.ToDictionary(p => p.Key, p => p.Key == "data"
            ? data.RootElement.Clone() : JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);
        return (await control.ExecuteAsync(arguments, Risk, cancellationToken)).GetRawText();
    }
}
