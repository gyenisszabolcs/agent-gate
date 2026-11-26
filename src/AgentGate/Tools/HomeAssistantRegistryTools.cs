using System.ComponentModel;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Audit;
using AgentGate.Policy;
using ModelContextProtocol.Server;

namespace AgentGate.Tools;

[McpServerToolType]
public sealed class HomeAssistantRegistryTools(IServiceProvider services)
{
    [McpServerTool(Name = "ha_get_labels"), Description("Home Assistant címkék azonosítója és neve; legfeljebb 100 címke.")]
    [ToolRisk(RiskLevel.Read), ToolAuditParameters]
    public async Task<string> Labels(CancellationToken cancellationToken) =>
        (await (services.GetService<HomeAssistantClient>() ?? throw new GateToolException("A Home Assistant nincs konfigurálva.")).GetLabelsAsync(cancellationToken)).GetRawText();

    [McpServerTool(Name = "ha_update_registry"), Description("Egy entitás registry-metaadatainak módosítása saját gate-jóváhagyással. A patch csak area_id, labels_add, labels_remove, name, icon, hidden_by, disabled_by mezőt fogad. A cél saját area_id-ját küldd: azonosítót, nem területnevet. Példa: {\"entity_id\":\"light.yeelight_nappali\",\"patch\":{\"area_id\":\"nappali\"}}. Öröklött before állapotot ne küldj: a gate maga olvassa az entitás saját területét és eszközkapcsolatát, és oldja fel az eszközről örökölt területet. A patch.area_id:null a saját területet törli, így visszaáll az öröklésre, ha van eszközterület. A hiányzó mező változatlan; más mezőn az explicit null visszaállítás. Eszközcél (device_id) továbbra is tiltott.")]
    [ToolRisk(RiskLevel.Configuration), ToolAuditParameters("entity_id", "patch", "before", "after", "changes")]
    public string Update(JsonElement patch, string? entity_id = null, string? device_id = null) =>
        throw new GateToolException("Ez az eszköz kizárólag a gate jóváhagyási útvonalán indítható.");
}
