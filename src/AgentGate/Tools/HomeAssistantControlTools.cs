using System.ComponentModel;
using System.Text.Json;
using AgentGate.Audit;
using AgentGate.Policy;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentGate.Tools;

[McpServerToolType]
public sealed class HomeAssistantControlTools(AgentGate.Integrations.HomeAssistant.Control.HomeAssistantControlService control)
{
    [ToolAuditParameters("domain", "service", "entity_id", "data")]
    [ToolRisk(RiskLevel.Control)]
    [McpServerTool(Name = "ha_control", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Egy engedélyezett Home Assistant entitás szűk vezérlése.")]
    public async Task<CallToolResult> Control(string domain, string service, string entity_id, CancellationToken cancellationToken, JsonElement? data = null)
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["domain"] = JsonSerializer.SerializeToElement(domain),
            ["service"] = JsonSerializer.SerializeToElement(service),
            ["entity_id"] = JsonSerializer.SerializeToElement(entity_id),
            ["data"] = data ?? JsonSerializer.SerializeToElement<object?>(null)
        };
        var result = await control.ExecuteAsync(arguments, RiskLevel.Control, cancellationToken);
        return new CallToolResult { StructuredContent = result, Content = [new TextContentBlock { Text = result.GetRawText() }] };
    }

    [ToolAuditParameters("domain", "service", "entity_id", "data")]
    [ToolRisk(RiskLevel.SensitiveControl)]
    [McpServerTool(Name = "ha_sensitive_control", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Egy engedélyezett zár vagy érzékeny cover vezérlése a gate jóváhagyásával.")]
    public Task<CallToolResult> SensitiveControl(string domain, string service, string entity_id, CancellationToken cancellationToken, JsonElement? data = null) =>
        throw new GateToolException("A művelet csak a jóváhagyási kapun hajtható végre.");
}
