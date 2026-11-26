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
        var (request, name) = await control.PrepareApprovalAsync(arguments, Risk, cancellationToken);
        var parameters = new Dictionary<string, string?>
        {
            ["domain"] = request.Domain,
            ["service"] = request.Service,
            ["entity_id"] = request.EntityId,
            ["data"] = request.Data.GetRawText()
        };
        if (request.Domain == "media_player" && request.Service == "speak")
        {
            parameters["tts_engine"] = policy.Media.EngineEntityId;
            if (!request.Data.TryGetProperty("volume_level", out _))
            {
                parameters["tts_max_volume"] = policy.Media.MaximumVolume.GetRawText();
                parameters["tts_fallback_volume"] = policy.Media.FallbackVolume.GetRawText();
            }
        }
        return new PreparedOperation(Tool, Risk, parameters, presentation: new ApprovalPresentation(name));
    }
    public async Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken)
    {
        var tts = operation.Parameters.GetValueOrDefault("domain") == "media_player" && operation.Parameters.GetValueOrDefault("service") == "speak";
        using var data = JsonDocument.Parse(operation.Parameters["data"]!);
        var implicitVolume = tts && !data.RootElement.TryGetProperty("volume_level", out _);
        if (operation.Tool != Tool || operation.Risk != Risk || operation.Parameters.Count != (implicitVolume ? 7 : tts ? 5 : 4) ||
            implicitVolume && (operation.Parameters.GetValueOrDefault("tts_max_volume") != policy.Media.MaximumVolume.GetRawText() ||
                operation.Parameters.GetValueOrDefault("tts_fallback_volume") != policy.Media.FallbackVolume.GetRawText()) ||
            tts && operation.Parameters.GetValueOrDefault("tts_engine") != policy.Media.EngineEntityId) throw ControlPolicy.Denied();
        var arguments = operation.Parameters.Where(p => p.Key is not ("tts_engine" or "tts_max_volume" or "tts_fallback_volume")).ToDictionary(p => p.Key, p => p.Key == "data"
            ? data.RootElement.Clone() : JsonSerializer.SerializeToElement(p.Value), StringComparer.Ordinal);
        return (await control.ExecuteAsync(arguments, Risk, cancellationToken)).GetRawText();
    }
}
