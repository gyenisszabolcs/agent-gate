using System.Text.Json;

namespace AgentGate.Policy;

/// <summary>A modul nyers MCP-argumentumainak ellenőrzése és szűk auditkiegészítése; nem ad végrehajtási jogot.</summary>
public interface IToolCallPolicy
{
    bool Handles(string tool);
    void ValidateArguments(IReadOnlyDictionary<string, JsonElement> arguments);
    void EnrichAuditParameters(IReadOnlyDictionary<string, JsonElement> arguments, RiskLevel risk,
        IDictionary<string, string?> parameters);
}
