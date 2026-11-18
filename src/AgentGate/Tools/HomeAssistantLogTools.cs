using System.ComponentModel;
using System.Text.RegularExpressions;
using AgentGate.Audit;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Integrations.HomeAssistant.LogRead;
using AgentGate.Policy;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentGate.Tools;

[McpServerToolType]
public sealed class HomeAssistantLogTools(IServiceProvider services)
{
    [ToolAuditParameters("limit", "level", "integration")]
    [ToolRisk(RiskLevel.Read)]
    [McpServerTool(Name = "ha_get_logs", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("A HA hibanapló legutóbbi bejegyzései, régebbitől újabbig. Alaplimit 20, maximum 100. Szint: DEBUG/INFO/WARNING/ERROR/CRITICAL; integration: pontos integrációazonosító. Konzervatív titokvédelem teljes kitakarást is okozhat.")]
    public async Task<CallToolResult> GetLogs(CancellationToken cancellationToken, int limit = 20, string? level = null, string? integration = null)
    {
        if (limit is < 1 or > 100)
            throw new GateToolException("Érvénytelen naplólimit; 1–100 közötti egész szám szükséges.");
        if (level is not null && level is not ("DEBUG" or "INFO" or "WARNING" or "ERROR" or "CRITICAL"))
            throw new GateToolException("Érvénytelen naplószint.");
        if (integration is not null && (integration.Length is < 1 or > 64 ||
            !Regex.IsMatch(integration, @"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant)))
            throw new GateToolException("Érvénytelen integrációszűrő.");
        var client = services.GetService<HomeAssistantClient>() ?? throw new GateToolException("A Home Assistant nincs beállítva.");
        HomeAssistantLogSnapshot snapshot;
        try { snapshot = await client.GetLogsAsync(cancellationToken); }
        catch (HomeAssistantException) { throw new GateToolException("A Home Assistant naplólekérés sikertelen."); }
        cancellationToken.ThrowIfCancellationRequested();
        HomeAssistantLogEntry[] matches;
        try
        {
            matches = HomeAssistantLogParser.Parse(snapshot.Text, cancellationToken)
                .Where(item => (level is null || item.Level == level) && (integration is null || item.Integration == integration)).ToArray();
        }
        catch (RegexMatchTimeoutException) { throw new GateToolException("A Home Assistant naplólekérés sikertelen."); }
        var items = matches.TakeLast(limit).ToList();
        return HomeAssistantLogProjection.Create(items, matches.Length > limit || snapshot.Redacted, snapshot.Redacted, cancellationToken);
    }
}
