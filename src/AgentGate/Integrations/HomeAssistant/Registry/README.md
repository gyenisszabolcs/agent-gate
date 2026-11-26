# Home Assistant nyilvántartási műveletek

A modul a meglévő gate jóváhagyási és auditútvonalát használja. Nincs általános websocket-, sablon- vagy HTTP-átjáró, csoportos módosítás, új terület/címke létrehozása vagy törlése, illetve entitásazonosító-átnevezés.

## Eszközök és bekapcsolás

- `ha_update_registry(patch, entity_id vagy device_id)`: pontosan egy entitás vagy eszköz nyilvántartási metaadatai, **Configuration** kockázattal. Minden írás külön, egyszer használható és lejáró, HA pushos gate-jóváhagyáshoz kötött. Az MCP-eszköz közvetlen metódusa nem végez írást.
- `ha_get_labels`: **Read** kockázatú címkeolvasás, legfeljebb 100 biztonságosan kiadható elem, `label_id` és `name` mezőkkel. A JSON-válasz `items`, `returned_count`, `truncated` mezőket tartalmaz; nincs lapozás. Ez az olvasás nem igényli a registry-írás engedélyezését.
- `HomeAssistantRegistry:IncludeEntityLabels=true` mellett a `ha_get_state` és `ha_list_states` eredményeiben megjelenik a `labels` azonosítólista is. Ez további, entitásonkénti registry websocket-lekérést igényel; hiba esetén nincs részleges siker. Kikapcsolva a korábbi állapotválasz-séma marad.

A működéshez a meglévő `HomeAssistant` kliens, a Bitwardenben tárolt HA-token, az audit és az írásokhoz a rendelkezésre álló HA-jóváhagyási csatorna szükséges. A telepítési modell a már elfogadott dedikált admin HA-felhasználót használja; a modul nem ad új jogosultságot és nem kér tokent a hívótól. A websocket ugyanabból az ellenőrzött originből származik, mint a kliens: HTTPS→WSS, kizárólag a meglévő explicit privát IPv4 HTTP-kivételnél HTTP→WS. A titkosítatlan kivétel kockázatai változatlanok.

### Új konfigurációs kulcsok

- `HomeAssistantRegistry:Enabled`: boolean, alapérték `false`. Csak a registry-írást engedélyezi; nem helyettesíti a domainengedélyezést, jóváhagyást vagy vészleállítást.
- `HomeAssistantRegistry:IncludeEntityLabels`: boolean, alapérték `false`. Bekapcsolja a címkék kiegészítő olvasását az állapotlekérő eszközökben.
- `HomeAssistantRegistry:EntityDenylist`: pontos entitásazonosítók listája, alapból üres. Az írásnál a meglévő `HomeAssistantControl:EntityDenylist` tiltásával uniót képez, nem lazíthatja azt. Az olvasási `HomeAssistant:DomainAllowlist` is kötelező; üres lista továbbra is mindent tilt.

A [konfigurációs minta](appsettings.example.jsonc) kizárólag ezeket az új beállításokat mutatja. A meglévő HA-, titokszolgáltató-, audit-, kockázati és jóváhagyásicsatorna-beállításokat nem helyettesíti. Új titok vagy NuGet-függőség nincs. A beállítások indulási pillanatképek, változtatásuk újraindítást igényel. Hibás típus, ismeretlen kulcs vagy hibás lista fix konfigurációs hibát okoz; nincs csendes permisszív alapértékre visszaesés.

A `HomeAssistantRegistry` szakasz az `/etc/agentgate/agentgate.json` fájlban is engedélyezett. Ez nem lazítja sem a fájl meglévő ellenőrzéseit, sem a registry saját konfigurációvalidációját; az ismeretlen szakaszok továbbra is tiltottak.

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

Az `area_id` mindig az entitás kért **saját** területazonosítója, nem emberi területnév. Kliensoldali `before` paraméter nincs: a gate olvassa az entitás saját területét és eszközkapcsolatát, majd a `config/device_registry/list` validált `id`/`area_id` vetületéből feloldja az öröklést. A `patch.area_id:null` a saját beállítást törli; ha az eszköznek van területe, ismét azt örökli. A jóváhagyás például `Hálószoba (öröklött) → Nappali (saját)` változást mutat. Az audit és a hash-hez kötött elő-/utóállapot külön őrzi az `area_id`, `device_id`, `device_area_id`, `effective_area_id` és `area_source` (`entity`, `device`, `none`) értékeket. Bizonytalan vagy hiányos eszközadat elutasítást jelent, nem üres területet. Ez csak eszközadat-olvasás, nem eszközcélú írásengedély.

Területválasztáskor az eszköz összes entitásának közös területe eszközszintű beállítást, az eltérő saját entitásterületek pedig öröklésre visszaállítást (`area_id:null`) indokolnak. Az agent előbb ellenőrizze az eszközmódosítás sikerét; sikertelen vagy bizonytalan kimenetelre alapozva ne töröljön saját entitásterületet. Ha csak egy entitás van máshol, csak azt kell módosítani; eszköz nélküli entitásnál szintén az entitást. Bizonytalan esetben az agent kérdezzen. Nincs automatikus többcélú művelet; minden egyes módosítás külön, célhoz kötött gate-jóváhagyást és változatlan policy-ellenőrzést igényel.

Az elfogadott mezők kizárólag `area_id`, `labels_add`, `labels_remove`, `name`, `icon`, `hidden_by`, `disabled_by`. Ismeretlen vagy ismételt JSON-mező, hibás típus és hatás nélküli kérés elutasított. A név legfeljebb 80 karakter; az ikon legfeljebb 64 karakteres `mdi:` azonosító. A címkelisták és az eredő hozzárendelés legfeljebb 16 eleműek. Ezek rögzített implementációs határok, nem konfigurációs kulcsok. Nem auditálható, titok- vagy vezérlőkarakter-gyanús szövegnél elutasítás van, nem csendesen átírt szándék.

## Jóváhagyás, mentés és versenyhelyzet

A jóváhagyási push a cél emberi nevét (vagy fix semleges nevet) és a változó mezők teljes emberi régi/új értékét mutatja, technikai azonosítók nélkül. A két rövid sorba nem férő vagy hiánytalanul nem megjeleníthető változás fix, kisebb kérésekre bontást kérő elutasítás, nem csonkított jóváhagyás. Az ikonmódosítás jelenleg ilyen elutasítás. A részletek az auditban és a státuszútvonalon megmaradnak; a jóváhagyási hash a konkrét előállapothoz és változtatáshoz kötött. A gate még a jóváhagyási push előtt tartósan auditálja a teljes szükséges előállapotot és a kért eredményt. Ha ez nem őrizhető meg hiánytalanul és titokmentesen, nincs jóváhagyás és írás. A régi értékek kézi visszaállítás alapjai; automatikus rollback-eszköz nincs.

A „Gate eredmény” értesítés alapesetben két rövid magyar sora a tényleges gate-státuszt és a kért művelet emberi célját/változásait mutatja, többmezős változásnál is. Sikertelen felolvasásnál a végrehajtó zárt, teljes egyezéssel ellenőrzött hibakategóriája külön rövid sorban megmarad; tetszőleges eredményszöveg nem kerül bele. Csak az `Executed` állapot jelent „Kész” eredményt; az elutasítás, lejárat és sikertelen vagy bizonytalan kimenetel nem siker. A hibaüzenet nem jelent visszagörgetést. A push nem másolja a nyers végrehajtási eredményt, hívó-, eszköz-, entitás- vagy műveletazonosítót. A titokszűrés és a jóváhagyás megjelenítési korlátja az eredményre is érvényes; időközben biztonságosan nem megjeleníthető részleteknél semleges szöveg marad, a valódi státusz megtartásával. Az audit, a hash és a kötött paraméterek nem változnak.

A változó mezők emberi értékei NFC-normalizáltak; a katalógusnév-ütközés és az emberileg azonos régi/új érték mögötti valódi változás már előkészítéskor elutasított. A címkék emberi név szerint, NFC utáni ordinal sorrendben jelennek meg. A valódi `Nincs` terület-/címkenév és az `Alapértelmezett` entitásnév a változás bármely oldalán elutasított, hogy ne keveredjen az üres vagy alapértelmezett állapottal; maguk az ilyen állapotok és az `Igen`/`Nem` értékek továbbra is használhatók. Ez nem általános Unicode-homográfszűrés. A kötött szándék és a hash bemenete nem normalizált, a megjelenítési adatok továbbra sem részei.

Végrehajtáskor a gate újra előkészíti és összeveti a műveletet; az írási websocket hitelesítése után, a parancs elküldése előtt ismét beolvassa az entitás saját területét, eszközkapcsolatát és az eszköz területét is tartalmazó előállapotot, ellenőrzi az entitásengedélyt és a vészleállítót. Észlelt eltérés esetén **nem ír**: új kérés és új jóváhagyás szükséges. A tiltott entitás, a tiltott/érvénytelen mező vagy bemenet, illetve a megváltozott előállapot külön, rögzített titokmentes hibaokot kap; nyers HA-hiba nem jut a válaszba. Az írás nincs automatikusan újrapróbálva; a kapcsolat megszakadása a parancs elküldése után bizonytalan eredményt jelenthet, ezért kézi HA-ellenőrzés szükséges új kérés előtt.

**Nincs teljes atomi védelem.** A HA registry update API nem kínál elvárt-régi-értékhez vagy verzióhoz kötött atomi írást. Az újraolvasás és az írás közötti időrésben más HA-kliens továbbra is módosíthatja az állapotot. A gate saját ellenőrzése és zárolása nem zárja ki a külső írót; a jóváhagyott garancia best-effort újraellenőrzés, nem compare-and-set és nem HA-tranzakció.

## Eszközszintű módosítás

Példa: `{"device_id":"eszkoz_azonosito","patch":{"area_id":"nappali","name_by_user":"Olvasólámpa","labels":["hangulat"]}}`.
Csak `area_id`, `name_by_user`, `labels` írható a `config/device_registry/update` paranccsal. A `labels` a teljes kívánt címkelista (az üres lista töröl minden címke-hozzárendelést); minden kért címkének és területnek léteznie kell. A `name_by_user:null` az eredeti eszköznevet állítja vissza. Hiányzó mező változatlan. A `disabled_by` és minden más eszközmező tiltott; ez az út vezérlési jogot nem ad.

Az eszköz összes **aktív, nem letiltott** entitását a kódban rögzített `{{ device_entities("eszközazonosító") | to_json }}` sablon adja. A cél JSON-stringként kerül bele; nincs hívó által megadható sablon vagy általános templating eszköz. Minden aktív entitásra kötelező az olvasási domain-engedélyezés és mindkét entitástiltólista ellenőrzése; egy engedélyezett entitás nem elég. Üres/hibás válasz, sablonhiba, ismeretlen eszközmező vagy nem ellenőrizhető katalógus esetén nincs írás, állapotalapú helyettesítés vagy másik útvonal.

A HA Core **2026.11.0** `device_entities` helper az `async_entries_for_device` alapértelmezett `include_disabled_entities=False` ágát használja: a letiltott entitások kimaradnak és nem akadályozzák ezt a szűk metaadatírást. Ennek biztonsági határa, hogy kizárólag terület/név/címke változhat, vezérlési jog nem, és minden írás jóváhagyásköteles. Gyermekeszközre ugyanaz az ellenőrzés érvényes; összetett eszköz HA-oldali elutasítását nem követi újrapróbálás vagy szülőeszközre visszaesés.

A jóváhagyási diff minden megváltozó mezőt emberi régi/új értékkel mutat. Az audit és a kötött szándék megőrzi a régi értékeket és az aktív entitáshalmazt. Végrehajtáskor, majd az írási socket hitelesítése után is újraolvasás, minden aktív entitás policy-ellenőrzése, katalógusvalidálás és hash-összevetés történik; közvetlenül a küldés előtt a vészleállítót is ellenőrizzük. Az előző szakaszban leírt nem atomi versenyablak erre az írásra is vonatkozik.

Elsődleges kiadási források (2026.11.0 tag): [eszközrekordok](https://github.com/home-assistant/core/blob/2026.11.0/homeassistant/helpers/device_registry.py) (`DeviceEntry.dict_repr`, `ChildDeviceEntry.dict_repr`), [eszközfrissítés](https://github.com/home-assistant/core/blob/2026.11.0/homeassistant/components/config/device_registry.py), [registry segédfüggvények](https://github.com/home-assistant/core/blob/2026.11.0/homeassistant/helpers/entity_registry.py), [eszköz-sablonfüggvény](https://github.com/home-assistant/core/blob/2026.11.0/homeassistant/helpers/template/extensions/devices.py).
