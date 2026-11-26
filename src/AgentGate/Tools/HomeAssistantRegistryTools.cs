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

    [McpServerTool(Name = "ha_update_registry"), Description("Egy entitás vagy egy eszköz registry-metaadatainak módosítása saját gate-jóváhagyással. Pontosan egy cél kell: entity_id vagy device_id. Entitás patch: area_id, labels_add, labels_remove, name, icon, hidden_by, disabled_by. Eszköz patch: kizárólag area_id, name_by_user, labels; a labels a teljes új címkelista, a name_by_user:null az eredeti nevet állítja vissza; disabled_by és más eszközmező tiltott. A cél saját area_id-ját küldd: azonosítót, nem területnevet. Példa: {\"entity_id\":\"light.yeelight_nappali\",\"patch\":{\"area_id\":\"nappali\"}}. Öröklött before állapotot ne küldj: a gate maga olvassa az entitás saját területét és eszközkapcsolatát, és oldja fel az eszközről örökölt területet. A patch.area_id:null az entitás saját területét törli, így visszaáll az öröklésre, ha van eszközterület. A hiányzó mező változatlan. Eszközíráskor a rögzített device_entities(device_id) sablon összes aktív (nem letiltott) entitásának meg kell felelnie az olvasási engedélyezőlistának és a tiltólistának; üres lista, sablonhiba vagy nem ellenőrizhető adat esetén nincs írás. Ez gyermekeszközre is igaz; HA-elutasítást nem kerülünk meg. Területválasztási útmutató: ha egy eszköz összes entitása ugyanabba a területbe tartozik, az eszköz területét módosítsd device_id céllal, majd az eltérő saját entitásterületet patch.area_id:null értékkel állítsd öröklésre. Ellenőrizd előbb az eszközmódosítás sikerét; sikertelen vagy bizonytalan kimenetelre ne alapozz entitásmódosítást. Ha csak egy entitás van máshol, csak annak saját területét módosítsd; eszköz nélküli entitás esetén az entitást módosítsd; bizonytalan esetben kérdezz. Ez nem automatikus többcélú művelet: minden egyes módosítás külön célhoz kötött gate-jóváhagyást és policy-ellenőrzést igényel, megkerülés nélkül.")]
    [ToolRisk(RiskLevel.Configuration), ToolAuditParameters("entity_id", "device_id", "patch", "before", "after", "changes")]
    public string Update(JsonElement patch, string? entity_id = null, string? device_id = null) =>
        throw new GateToolException("Ez az eszköz kizárólag a gate jóváhagyási útvonalán indítható.");
}
