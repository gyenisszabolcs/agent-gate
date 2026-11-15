using System.Text.Json;

namespace AgentGate.Integrations.HomeAssistant;

public sealed record HomeAssistantConfig(JsonElement Data);
public sealed record HomeAssistantState(string EntityId, string State, JsonElement Attributes);
public sealed class HomeAssistantException(string message) : Exception(message);
