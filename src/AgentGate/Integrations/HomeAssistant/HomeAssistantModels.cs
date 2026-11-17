using System.Text.Json;

namespace AgentGate.Integrations.HomeAssistant;

public sealed record HomeAssistantConfig(JsonElement Data);
public sealed record HomeAssistantArea(string AreaId, string AreaName, IReadOnlyList<string> EntityIds);
public sealed record HomeAssistantState(string EntityId, string State, JsonElement Attributes, string? LastChanged = null);
public sealed class HomeAssistantException(string message) : Exception(message);
