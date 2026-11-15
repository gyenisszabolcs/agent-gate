using System.ComponentModel;
using AgentGate.Audit;
using AgentGate.Policy;
using ModelContextProtocol.Server;

namespace AgentGate.Tools;

[McpServerToolType]
public sealed class ApprovalTools
{
    [ToolAuditParameters("requestId")]
    [ToolRisk(RiskLevel.Read)]
    [McpServerTool(Name = "gate_approval_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("A konfigurált egyetlen hívó saját jóváhagyási kérésének szűrt állapota.")]
    public static string Status(string requestId, [Microsoft.AspNetCore.Mvc.FromServices] AgentGate.Approval.ApprovalGate gate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(requestId, "D", out var id) || gate.GetStatus(id) is not { } outcome)
            return "A kérés nem található.";
        return outcome.State switch
        {
            AgentGate.Approval.ApprovalState.Executed => "jóváhagyva és végrehajtva: " + outcome.Result,
            AgentGate.Approval.ApprovalState.Failed => "sikertelen: " + outcome.Result,
            AgentGate.Approval.ApprovalState.Rejected => "elutasítva",
            AgentGate.Approval.ApprovalState.Expired => "lejárt",
            _ => "várakozik"
        };
    }

    [ToolAuditParameters("text")]
    [ToolRisk(RiskLevel.SensitiveControl)]
    [McpServerTool(Name = "gate_approval_test", Destructive = false, OpenWorld = false)]
    [Description("Jóváhagyásköteles, éles mellékhatás nélküli próba, rövid szöveggel.")]
    public static string Test(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A közvetlen SDK-útvonal soha nem helyettesítheti a kapu tárolt műveletét.
        throw new GateToolException("A jóváhagyásköteles művelet csak a jóváhagyási kapun keresztül indítható.");
    }
}
