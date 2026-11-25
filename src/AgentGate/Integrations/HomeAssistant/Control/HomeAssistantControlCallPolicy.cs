using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Policy;
using AgentGate.Secrets;
using AgentGate.Tools;

namespace AgentGate.Integrations.HomeAssistant.Control;

/// <summary>A HA-vezérlés modulhoz tartozó MCP-ellenőrzések, auditmezők és kiadható hibakategóriák.</summary>
internal sealed class HomeAssistantControlCallPolicy(ControlPolicy policy) : IToolCallPolicy, IOperationErrorPolicy
{
    public bool Handles(string tool) => tool is "ha_control" or "ha_sensitive_control";

    public void ValidateArguments(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (arguments.Keys.Any(name => name is not ("domain" or "service" or "entity_id" or "data")))
            throw ControlPolicy.Denied();
    }

    public void EnrichAuditParameters(IReadOnlyDictionary<string, JsonElement> arguments, RiskLevel risk,
        IDictionary<string, string?> parameters)
    {
        // Csak e két eszköz teljesen validált, zárt sémájú skalárjai; nyers objektumot soha.
        parameters.Remove("data");
        try
        {
            var normalized = policy.Validate(arguments, risk);
            foreach (var field in normalized.Data.EnumerateObject())
                parameters["data." + field.Name] = field.Name == "message" ? SecretRedactor.RedactText(field.Value.GetString()) :
                    field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : field.Value.GetRawText();
            if (normalized.Domain == "media_player" && normalized.Service == "speak" &&
                arguments.TryGetValue("data", out var ttsData) && ttsData.TryGetProperty("message", out var text))
                parameters["data.text_length"] = text.GetString()!.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (GateToolException) { parameters["[kihagyva]"] = "[kihagyva]"; }
    }

    public string? GetSafeMessage(Exception exception, PreparedOperation? operation) => exception switch
    {
        ControlRateLimitException => ControlRateLimitException.SafeMessage,
        TtsQuietHoursException when operation is null => TtsQuietHoursException.SafeMessage,
        TtsExecutionException failure when operation is null ||
            operation.Tool == "ha_sensitive_control" &&
            operation.Parameters.GetValueOrDefault("domain") == "media_player" &&
            operation.Parameters.GetValueOrDefault("service") == "speak" => failure.SafeMessage,
        _ => null
    };
}
