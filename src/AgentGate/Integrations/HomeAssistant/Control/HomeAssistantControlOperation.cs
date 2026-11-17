using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Policy;

namespace AgentGate.Integrations.HomeAssistant.Control;

public sealed class HomeAssistantControlOperation(HomeAssistantControlService control, ControlPolicy policy) : IApprovalOperationHandler
{
    public string Tool => "ha_sensitive_control";
    public RiskLevel Risk => RiskLevel.SensitiveControl;
    public async ValueTask<PreparedOperation> PrepareAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        var request = await control.PrepareAsync(arguments, Risk, cancellationToken);
        var parameters = new Dictionary<string, string?>
        {
            ["domain"] = request.Domain,
            ["service"] = request.Service,
            ["entity_id"] = request.EntityId,
            ["data"] = request.Data.GetRawText()
        };
        if (request.Domain == "media_player" && request.Service == "speak") parameters["tts_engine"] = policy.Media.EngineEntityId;
        return new PreparedOperation(Tool, Risk, parameters);
    }
    public async Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken)
    {
        var tts = operation.Parameters.GetValueOrDefault("domain") == "media_player" && operation.Parameters.GetValueOrDefault("service") == "speak";
        if (operation.Tool != Tool || operation.Risk != Risk || operation.Parameters.Count != (tts ? 5 : 4) ||
            tts && operation.Parameters.GetValueOrDefault("tts_engine") != policy.Media.EngineEntityId) throw ControlPolicy.Denied();
        using var data = JsonDocument.Parse(operation.Parameters["data"]!);
        var arguments = operation.Parameters.Where(p => p.Key != "tts_engine").ToDictionary(p => p.Key, p => p.Key == "data"
            ? data.RootElement.Clone() : JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);
        return (await control.ExecuteAsync(arguments, Risk, cancellationToken)).GetRawText();
    }
}
