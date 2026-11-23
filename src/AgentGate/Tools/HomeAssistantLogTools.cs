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
    [Description("A HA összevont rendszernaplója, a LIFO forrás fordított sorrendjében (régebbitől újabbig). Alaplimit 20, maximum 100. Szint: WARNING/ERROR/CRITICAL, pontos egyezés; DEBUG/INFO nem elérhető. integration: pontos integrációazonosító. A szűrés utáni érzékeny bejegyzéseket a withheld_count jelzi.")]
    public async Task<CallToolResult> GetLogs(CancellationToken cancellationToken, int limit = 20, string? level = null, string? integration = null)
    {
        if (limit is < 1 or > 100)
            throw new GateToolException("Érvénytelen naplólimit; 1–100 közötti egész szám szükséges.");
        if (level is not null && level is not ("WARNING" or "ERROR" or "CRITICAL"))
            throw new GateToolException("Érvénytelen naplószint.");
        if (integration is not null && (integration.Length is < 1 or > 64 ||
            !Regex.IsMatch(integration, @"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant)))
            throw new GateToolException("Érvénytelen integrációszűrő.");
        var client = services.GetService<HomeAssistantClient>() ?? throw new GateToolException("A Home Assistant nincs beállítva.");
        HomeAssistantLogSnapshot snapshot;
        try { snapshot = await client.GetLogsAsync(cancellationToken, level, integration); }
        catch (HomeAssistantException) { throw new GateToolException("A Home Assistant naplólekérés sikertelen."); }
        cancellationToken.ThrowIfCancellationRequested();
        var items = snapshot.Items.TakeLast(limit).ToList();
        return HomeAssistantLogProjection.Create(items, snapshot.Items.Count > limit || snapshot.WithheldCount > 0,
            snapshot.Redacted, snapshot.WithheldCount, cancellationToken);
    }
}
