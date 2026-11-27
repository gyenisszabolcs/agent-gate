using System.ComponentModel;
using AgentGate.Audit;
using AgentGate.Policy;
using ModelContextProtocol.Server;

namespace AgentGate.Tools;

[McpServerToolType]
public sealed class ApprovalTools
{
    [ToolAuditParameters("requestId", "wait_seconds")]
    [ToolRisk(RiskLevel.Read)]
    [McpServerTool(Name = "gate_approval_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("A konfigurált hívó saját jóváhagyási kérésének állapota. Jóváhagyási kérés után ezzel várjon a döntésre, és annak megfelelően folytassa. wait_seconds: opcionális egész 0..120, alapérték 0 (azonnali); pozitív értéknél legfeljebb ennyi másodpercet vár a döntésre vagy a végrehajtási eredményre. A jóváhagyás még nem végrehajtási siker: ha az eredményre is szükség van, kérdezze le újra ezt a státuszt. Időtúllépéskor az aktuális állapot tér vissza; új státuszhívást indítson, ne az eredeti műveletet küldje újra. A várakozás sem indít, sem ismétel műveletet.")]
    public static async Task<string> Status(string requestId, [Microsoft.AspNetCore.Mvc.FromServices] AgentGate.Approval.ApprovalGate gate,
        CancellationToken cancellationToken,
        [Description("Jóváhagyáskérés után legfeljebb ennyi másodpercig várjon döntésre vagy eredményre (0–120, alapértelmezett 0), majd a döntés szerint folytassa. A jóváhagyás nem végrehajtási siker: szükség esetén az eredményt is várja meg. Időkorlát után új státuszhívás, nem a művelet újraküldése szükséges.")] int wait_seconds = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (wait_seconds is < 0 or > 120) throw new GateToolException("A wait_seconds csak 0 és 120 közötti egész szám lehet.");
        if (!Guid.TryParseExact(requestId, "D", out var id) || await gate.GetStatusAsync(id, wait_seconds, cancellationToken) is not { } outcome)
            return "A kérés nem található.";
        var message = outcome.State switch
        {
            AgentGate.Approval.ApprovalState.Executed => "jóváhagyva és végrehajtva: " + outcome.Result,
            AgentGate.Approval.ApprovalState.Failed => "sikertelen: " + outcome.Result,
            AgentGate.Approval.ApprovalState.Rejected => "elutasítva",
            AgentGate.Approval.ApprovalState.Expired => "lejárt",
            AgentGate.Approval.ApprovalState.Approved => "jóváhagyva; végrehajtásra vagy eredményre várakozik (ez még nem siker)",
            _ => "várakozik"
        };
        return outcome.Items is null ? message : message + "\n" + System.Text.Json.JsonSerializer.Serialize(new
        {
            items = outcome.Items, progress = outcome.Progress
        }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
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
