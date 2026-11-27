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

    [McpServerTool(Name = "ha_update_registry"), Description(
        "Registry-metaadatok módosítása vagy új címke létrehozása gate-jóváhagyással. Egy cél: entity_id vagy device_id és patch; vagy önálló create_label: kötelező name, opcionális mdi: icon és HA-témaszín/#RRGGBB color, más mező nélkül. Névütközés tiltott, címkemódosítás/törlés nincs. " +
        "Alternatíva az items: 1–50 konkrét entity_id/device_id és patch vagy create_label tag, rögzített sorrendben. A create is egy tag. Csak csomagban a patch.labels_add_created korábbi create_label tagok nulla alapú items indexét tartalmazza: az adott create hash-éhez kötve, kizárólag annak validált HA-válasz-ID-jára oldjuk fel, név alapján soha. Entitásnál hozzáadás; eszköznél a labels teljes listáját egészíti ki. Egyetlen kötött jóváhagyás kell a létrehozáshoz és hozzárendeléshez. Példa: {\"items\":[{\"create_label\":{\"name\":\"Esti fény\"}},{\"entity_id\":\"light.nappali\",\"patch\":{\"labels_add_created\":[0]}}]}. " +
        "Azonos cél nem ismételhető, dinamikus kiválasztó nincs. Entitás patch: area_id, labels_add, labels_remove, name, icon, hidden_by, disabled_by. Eszköz patch: kizárólag area_id, name_by_user, labels; a labels a teljes új címkelista, a name_by_user:null az eredeti nevet állítja vissza; disabled_by és más eszközmező tiltott. A cél saját area_id-ját küldd: azonosítót, nem területnevet. Példa: {\"entity_id\":\"light.yeelight_nappali\",\"patch\":{\"area_id\":\"nappali\"}}. Öröklött before állapotot ne küldj: a gate maga olvassa az entitás saját területét és eszközkapcsolatát, és oldja fel az eszközről örökölt területet. A patch.area_id:null az entitás saját területét törli, így visszaáll az öröklésre, ha van eszközterület. A hiányzó mező változatlan. " +
        "Eszközíráskor a rögzített device_entities(device_id) sablon összes aktív (nem letiltott) entitásának meg kell felelnie az olvasási engedélyezőlistának és a tiltólistának; üres lista, sablonhiba vagy nem ellenőrizhető adat esetén nincs írás. Ez gyermekeszközre is igaz; HA-elutasítást nem kerülünk meg. Területválasztási útmutató: ha egy eszköz összes entitása ugyanabba a területbe tartozik, az eszköz területét módosítsd device_id céllal, majd az eltérő saját entitásterületet patch.area_id:null értékkel állítsd öröklésre. Ellenőrizd előbb az eszközmódosítás sikerét; sikertelen vagy bizonytalan kimenetelre ne alapozz entitásmódosítást. Ha csak egy entitás van máshol, csak annak saját területét módosítsd; eszköz nélküli entitás esetén az entitást módosítsd; bizonytalan esetben kérdezz. Függő eszköz/entitásterület-lépéseket külön kérj, az első igazolt sikere után. " +
        "A teljes, név szerinti előtte→utána lista egyetlen HA értesítésben jelenik meg; hiányos megjelenítés nem hagyható jóvá. Egy közös, egyszer használható, lejáró jóváhagyás a teljes cél-, sorrend- és előállapot-hash-hez kötött; minden tag külön policy-ellenőrzést kap. Végrehajtás sorrendben, tételenként újraellenőrzéssel; első hiba, eltérés, megszakítás, vészleállítás vagy audit-hiba után stop. Nem tranzakció: nincs rollback/retry; a már létrejött címke megmarad. Az eredményt és a teljes listát gate_approval_status adja; bizonytalan részt ne ismételj vakon.")]
    [ToolRisk(RiskLevel.Configuration), ToolAuditParameters("entity_id", "device_id", "patch", "before", "after", "changes", "items", "create_label")]
    public string Update(JsonElement? patch = null, string? entity_id = null, string? device_id = null, JsonElement? items = null, JsonElement? create_label = null) =>
        throw new GateToolException("Ez az eszköz kizárólag a gate jóváhagyási útvonalán indítható.");
}
