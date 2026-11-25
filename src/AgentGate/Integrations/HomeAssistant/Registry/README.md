# Home Assistant nyilvántartási műveletek

A modul a meglévő gate jóváhagyási és auditútvonalát használja. Nincs általános websocket-, sablon- vagy HTTP-átjáró, csoportos módosítás, új terület/címke létrehozása vagy törlése, illetve entitásazonosító-átnevezés.

## Eszközök és bekapcsolás

- `ha_update_registry(entity_id, patch)`: egy entitás nyilvántartási metaadatai, **Configuration** kockázattal. Minden írás külön, egyszer használható és lejáró, HA pushos gate-jóváhagyáshoz kötött. Az MCP-eszköz közvetlen metódusa nem végez írást.
- `ha_get_labels`: **Read** kockázatú címkeolvasás, legfeljebb 100 biztonságosan kiadható elem, `label_id` és `name` mezőkkel. A JSON-válasz `items`, `returned_count`, `truncated` mezőket tartalmaz; nincs lapozás. Ez az olvasás nem igényli a registry-írás engedélyezését.
- `HomeAssistantRegistry:IncludeEntityLabels=true` mellett a `ha_get_state` és `ha_list_states` eredményeiben megjelenik a `labels` azonosítólista is. Ez további, entitásonkénti registry websocket-lekérést igényel; hiba esetén nincs részleges siker. Kikapcsolva a korábbi állapotválasz-séma marad.

A működéshez a meglévő `HomeAssistant` kliens, a Bitwardenben tárolt HA-token, az audit és az írásokhoz a rendelkezésre álló HA-jóváhagyási csatorna szükséges. A telepítési modell a már elfogadott dedikált admin HA-felhasználót használja; a modul nem ad új jogosultságot és nem kér tokent a hívótól. A websocket ugyanabból az ellenőrzött originből származik, mint a kliens: HTTPS→WSS, kizárólag a meglévő explicit privát IPv4 HTTP-kivételnél HTTP→WS. A titkosítatlan kivétel kockázatai változatlanok.

### Új konfigurációs kulcsok

- `HomeAssistantRegistry:Enabled`: boolean, alapérték `false`. Csak a registry-írást engedélyezi; nem helyettesíti a domainengedélyezést, jóváhagyást vagy vészleállítást.
- `HomeAssistantRegistry:IncludeEntityLabels`: boolean, alapérték `false`. Bekapcsolja a címkék kiegészítő olvasását az állapotlekérő eszközökben.
- `HomeAssistantRegistry:EntityDenylist`: pontos entitásazonosítók listája, alapból üres. Az írásnál a meglévő `HomeAssistantControl:EntityDenylist` tiltásával uniót képez, nem lazíthatja azt. Az olvasási `HomeAssistant:DomainAllowlist` is kötelező; üres lista továbbra is mindent tilt.

A [konfigurációs minta](appsettings.example.jsonc) kizárólag ezeket az új beállításokat mutatja. A meglévő HA-, titokszolgáltató-, audit-, kockázati és jóváhagyásicsatorna-beállításokat nem helyettesíti. Új titok vagy NuGet-függőség nincs. A beállítások indulási pillanatképek, változtatásuk újraindítást igényel. Hibás típus, ismeretlen kulcs vagy hibás lista fix konfigurációs hibát okoz; nincs csendes permisszív alapértékre visszaesés.

## Entitásszintű módosítás

Példa eszközargumentumok:

```json
{
  "entity_id": "light.nappali",
  "patch": {
    "area_id": "nappali",
    "labels_add": ["hangulat"],
    "labels_remove": [],
    "name": "Olvasólámpa",
    "icon": "mdi:floor-lamp",
    "hidden_by": null,
    "disabled_by": null
  }
}
```

A terület- és címkeazonosítók csak már létező HA-azonosítók lehetnek. A példa nem hoz létre ilyen objektumokat. Az explicit `null` az adott felülbírálás törlése; hiányzó mező változatlan marad. A címkehozzáadás és -elvétel megőrzi a többi hozzárendelést; ugyanaz a címke nem lehet mindkét listában. A `hidden_by` és `disabled_by` kért értéke kizárólag `"user"` vagy `null`; integráció, config entry vagy eszköz által birtokolt tiltást a modul nem ír felül.

Az elfogadott mezők kizárólag `area_id`, `labels_add`, `labels_remove`, `name`, `icon`, `hidden_by`, `disabled_by`. Ismeretlen vagy ismételt JSON-mező, hibás típus és hatás nélküli kérés elutasított. A név legfeljebb 80 karakter; az ikon legfeljebb 64 karakteres `mdi:` azonosító. A címkelisták és az eredő hozzárendelés legfeljebb 16 eleműek. Ezek rögzített implementációs határok, nem konfigurációs kulcsok. Nem auditálható, titok- vagy vezérlőkarakter-gyanús szövegnél elutasítás van, nem csendesen átírt szándék.

## Jóváhagyás, mentés és versenyhelyzet

A push a cél azonosítóját és a változó mezők régi/új értékét mutatja; a jóváhagyási hash a konkrét előállapothoz és változtatáshoz kötött. A gate még a jóváhagyási push előtt tartósan auditálja a teljes szükséges előállapotot és a kért eredményt. Ha ez nem őrizhető meg hiánytalanul és titokmentesen, nincs jóváhagyás és írás. A régi értékek kézi visszaállítás alapjai; automatikus rollback-eszköz nincs.

Végrehajtáskor a gate újra előkészíti és összeveti a műveletet; az írási websocket hitelesítése után, a parancs elküldése előtt ismét beolvassa az entitás előállapotát, ellenőrzi az entitásengedélyt és a vészleállítót. Észlelt eltérés esetén **nem ír**: új kérés és új jóváhagyás szükséges. Az írás nincs automatikusan újrapróbálva; a kapcsolat megszakadása a parancs elküldése után bizonytalan eredményt jelenthet, ezért kézi HA-ellenőrzés szükséges új kérés előtt.

**Nincs teljes atomi védelem.** A HA registry update API nem kínál elvárt-régi-értékhez vagy verzióhoz kötött atomi írást. Az újraolvasás és az írás közötti időrésben más HA-kliens továbbra is módosíthatja az állapotot. A gate saját ellenőrzése és zárolása nem zárja ki a külső írót; a jóváhagyott garancia best-effort újraellenőrzés, nem compare-and-set és nem HA-tranzakció.

## Eszközcél: jelenleg teljes elutasítás

Az elfogadott eszközszabály nem üres, bizonyíthatóan teljes kapcsolt entitáshalmazt követel, amelynek minden eleme megfelel az engedélyezésnek. Vegyes, üres vagy bizonytalan halmaz tiltott. Az aktuális adapter e teljességet nem tudja igazolni, ezért **minden `device_id` célú írást elutasít**; ez a változat nem kínál működő eszközszintű módosítást. Eszközről entitásokra automatikus kiterítés sincs.

A vizsgált HA Core-forrásban az `entity_registry/list` kihagyhat nem szerializálható rekordokat; a `list_for_display` letiltott bejegyzéseket is szűr. A fix `device_entities` sablonfüggvény a letiltottakat alapból kihagyó lekérdezést használja, a `get_entries` pedig csak az előre ismert azonosítókat oldja fel. E válaszok sikeressége önmagában nem bizonyít teljes kapcsolathalmazt. A fejlesztéshez vizsgált upstream `dev` forrás nem az éles HA-verzió igazolása.

**A szigorú engedélyezés mellett a legtöbb eszköz csak entitásszinten lesz módosítható; a jelenlegi adapterrel ez minden eszközre igaz.** A korlátozás feloldásához megbízható teljességigazolás és külön ellenőrzött megvalósítás kell, nem a jogosultsági szabály lazítása.

Elsődleges források: [registry websocket parancsok](https://github.com/home-assistant/core/blob/dev/homeassistant/components/config/entity_registry.py), [registry segédfüggvények](https://github.com/home-assistant/core/blob/dev/homeassistant/helpers/entity_registry.py), [eszköz-sablonfüggvény](https://github.com/home-assistant/core/blob/dev/homeassistant/helpers/template/extensions/devices.py).
