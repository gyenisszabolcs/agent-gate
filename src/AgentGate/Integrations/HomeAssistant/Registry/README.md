# Home Assistant nyilvántartási műveletek

A modul a meglévő gate jóváhagyási és auditútvonalát használja. Nincs általános websocket-, sablon- vagy HTTP-átjáró, új terület létrehozása vagy törlése, címkemódosítás/-törlés, illetve entitásazonosító-átnevezés. Címkelétrehozás és csoportos írás kizárólag a szűk `ha_update_registry` művelettel kérhető.

## Eszközök és bekapcsolás

- `ha_update_registry(patch, entity_id vagy device_id)`: egy entitás vagy eszköz nyilvántartási metaadatai, **Configuration** kockázattal. A meglévő egycélú API változatlan; alternatív `items` bemenettel 1–50 konkrét cél egy csomagba fogható az alábbi szabályok szerint. Minden kérés egyszer használható és lejáró, HA pushos gate-jóváhagyáshoz kötött. Az MCP-eszköz közvetlen metódusa nem végez írást.
- `ha_get_labels`: **Read** kockázatú címkeolvasás, legfeljebb 100 biztonságosan kiadható elem, `label_id` és `name` mezőkkel. A JSON-válasz `items`, `returned_count`, `truncated` mezőket tartalmaz; nincs lapozás. Ez az olvasás nem igényli a registry-írás engedélyezését.
- `HomeAssistantRegistry:IncludeEntityLabels=true` mellett a `ha_get_state` és `ha_list_states` eredményeiben megjelenik a `labels` azonosítólista is. Ez további, entitásonkénti registry websocket-lekérést igényel; hiba esetén nincs részleges siker. Kikapcsolva a korábbi állapotválasz-séma marad.

### Csoportos registry-módosítás

Példa a `ha_update_registry` argumentumaira:

```json
{"items":[{"entity_id":"sensor.nappali","patch":{"name":"Nappali érzékelő"}},{"device_id":"device_123","patch":{"name_by_user":"Kerti érzékelő"}}]}
```

- Vagy `items`, vagy a meglévő egycélú `entity_id`/`device_id` és `patch`, vagy önálló `create_label` adható meg, együtt soha. Egy tag konkrét cél és `patch`, vagy az alább leírt `create_label`; 1–50 tag, a létrehozás is egy tag, azonos cél nem ismételhető. Nincs wildcard vagy automatikus kiválasztás. A meglévő mező-, domain- és entitás-engedélyezőlista, tiltólista, eszköz-aktíventitás-vizsgálat és vészleállítás minden tagra érvényes.
- A gate minden előállapotot maga olvas. A normalizált célok, sorrend, teljes előállapot, szándék és tényleges változások egyetlen tulajdonoshoz kötött hash részei. Egyetlen, lejáró, egyszer használható jóváhagyás engedi a teljes csomagot; egy részhalmaz külön nem hagyható jóvá.
- A HA egyetlen tartós értesítésében jelenik meg a teljes, sorszámozott, név és konkrét célazonosító szerinti előtte→utána lista. A rövid push csak a darabszámot, kérőt, lejáratot és a HA értesítéseire utalást tartalmazza. Döntési gomb csak a teljes tartós értesítés sikeres létrehozása után használható. Hiányzó részlet, titokgyanús adat vagy nem megjeleníthető érték esetén nincs jóváhagyható kérés; az elutasítás biztonságos okot ad. A túl nagy teljes listát kisebb csomagokra kell bontani. A teljes részlet rögzített felső korlátja 32 768 UTF-8 byte, ezt már előkészítéskor ellenőrizzük; nincs csonkítás és nincs új konfigurációs kulcs.
- A `gate_approval_status(requestId, wait_seconds)` a tulajdonosnak a teljes `items` listát adja a státuszszöveg utáni JSON-ban, a konkrét célokkal, nevekkel, biztonságos előállapot/szándék/változás adatokkal és emberi megjelenítéssel. Az idegen vagy ismeretlen kérés továbbra sem látható. Az egycélú szöveges státusz nem változott. A végállapot csak a csomag- és eredeti audit lezárása után publikálható; a köztes jóváhagyás nem végrehajtási siker.
- A végrehajtás rögzített sorrendű. Minden tag előtt újraolvasás és előállapot-egyezés, valamint közvetlen írás előtti cancel/kill/policy ellenőrzés történik. Az első eltérés, HA-hiba, megszakítás, vészleállítás vagy audit-hiba megállítja a csomagot. Nincs rollback, automatikus retry vagy folytatás. Az addig elkészült változások megmaradnak. Az upstream HA registry API nem kínál CAS-t: az utolsó ellenőrzés és az írás között fennmarad egy versenyablak; elveszett HTTP/websocket válasz esetén a hatás bizonytalan lehet.
- Az eredményben `progress` adja a `total`, `completed`, `stoppedAt` (1-től számozott), `failed`, `notStarted` számlálókat és a zárt hibakategóriát; az egyes elemek `state` értéke `Succeeded`, `Failed` vagy `NotStarted`. A `Failed` bizonytalan hatást is jelenthet, nem visszagörgetést. A Gate eredmény push a kész darabszámot, megállási helyet és hátralévő darabszámot mutatja, nem provider-hibaszöveget. Csomagaudit-hiba után az összállapot sikertelen akkor is, ha minden tag elkészült. Soha ne küldd újra vakon az egész csomagot: ellenőrizd az érintett célokat, és új jóváhagyást kérj a valóban szükséges változásokra.
- A csomag előkészítésének teljes adatai/hash-e, végrehajtása és eredménye auditált. Minden végrehajtott vagy előállapot-ellenőrzésen megállt tag külön auditot kap a régi értékekkel, sorszámmal és közös `requestId` korrelációval; a kihagyott tagok külön `NotStarted` sorokat kapnak. Audit-hiba esetén a további audit sem garantálható, de siker és további HA-írás nem engedett. A lezárás korlátos és nem ismétli meg a meghiúsult auditírást.
- Ne tegyél ugyanabba a csomagba egymástól függő területmódosításokat: ha az első eszközmódosítás megváltoztatja egy későbbi entitás öröklött előállapotát, a csomag helyesen eltérésnél megáll. Előbb várd meg az első kérés igazolt sikerét, utána kérd a következőt.

A működéshez a meglévő `HomeAssistant` kliens, a Bitwardenben tárolt HA-token, az audit és az írásokhoz a rendelkezésre álló HA-jóváhagyási csatorna szükséges. A telepítési modell a már elfogadott dedikált admin HA-felhasználót használja; a modul nem ad új jogosultságot és nem kér tokent a hívótól. A websocket ugyanabból az ellenőrzött originből származik, mint a kliens: HTTPS→WSS, kizárólag a meglévő explicit privát IPv4 HTTP-kivételnél HTTP→WS. A titkosítatlan kivétel kockázatai változatlanok.

### Új címke, opcionálisan hozzárendeléssel

Önálló példa: `{"create_label":{"name":"Esti fény","icon":"mdi:tag","color":"blue"}}`.
A név kötelező, legfeljebb 80 karakter; az opcionális ikon legfeljebb 64 karakteres `mdi:` azonosító. A szín HA-témaszín (például `blue`, `red`, `primary`) vagy `#RRGGBB`; tetszőleges CSS, leírás vagy más mező nem adható meg. Az opcionális mezők `null` értéke nem kér tulajdonságot. A név és minden megadott tulajdonság a hash, a diff és az audit része. Az előállapot `null` (nem létező címke), a push címe `Jóváhagyás: új címke · <név>`. Ez is **Configuration**, ugyanazzal a jóváhagyási, tulajdonos-, titokszűrési és vészleállítási védelemmel.

Egy jóváhagyással létrehozás, majd konkrét hozzárendelés:

```json
{"items":[{"create_label":{"name":"Esti fény"}},{"entity_id":"light.nappali","patch":{"labels_add_created":[0]}}]}
```

A `labels_add_created` kizárólag csomagon belüli, nulla alapú hivatkozás korábbi `create_label` tagokra a teljes `items` listában, nem a létrehozások sorszáma; nincs név szerinti keresés. Például create, hozzárendelés, create, hozzárendelés, hozzárendelés sorrendnél a két create indexe 0 és 2: az első hozzárendelés `[0]`, az utolsó kettő `[2]`. A gate a hivatkozást az adott create hash-éhez köti, és csak annak validált HA-válaszából kapott azonosítóra oldja fel. Előre-, ön-, nem create tagra mutató, nem létező és ismételt referencia külön, érthető hivatkozási hibával elutasított, nem méretüzenettel. Entitásnál a meglévő címkék megmaradnak; eszköznél a `labels` továbbra is teljes kívánt lista, ehhez adódnak az új hivatkozások (például `{"device_id":"device_123","patch":{"labels":["megtartott_id"],"labels_add_created":[0]}}`). Azonos cél csak egyszer szerepelhet. A belső `gate_pending_` névtér nem használható bemeneti címkeazonosítóként. A teljes lista és függőség a szokásos HA tartós értesítésben/státuszban látszik; a rövid csomagpush változatlan.

A névütközés kulcsa NFC-normalizálás, Unicode 17.0 teljes (C/F) case folding, majd az ASCII szóközök elhagyása; a beküldött nevet NFC alakban kötjük és küldjük. Ez a HA 2026.11.0 `casefold().replace(" ", "")` ellenőrzésénél az NFC miatt szigorúbb, nem homográfszűrés. Ütközést előkészítéskor, végrehajtáskor és az írási socket hitelesítése után is vizsgálunk. A katalógus azonosító/név/ikon/szín/leírás vetülete is rögzített; kizárólag a saját, igazolt create helyőrzőjének feloldása megengedett, idegen változás nem kap felmentést.

A [HA create API](https://github.com/home-assistant/core/blob/2026.11.0/homeassistant/components/config/label_registry.py) nem kínál elvárt katalógusverzióhoz kötött CAS-t vagy csomagtranzakciót. A külső íróval szembeni versenyablak megmarad; elveszett/hibás create-válasz után nincs hozzárendelés, retry vagy rollback. A már létrehozott címke részleges sikerben megmarad. A validált ID külön auditsorban szerepel közös `requestId`, create-hash és csomagpozíció korrelációval, régi címkeérték nélkül. Bizonytalan hatás után előbb kézi HA-ellenőrzés, majd szükség esetén új kérés kell.

Élesítés: a címkelétrehozás nem vezet be új konfigurációs kulcsot, titkot, függőséget vagy jogosultságot. A meglévő `/etc/agentgate/agentgate.json` `HomeAssistantRegistry:Enabled=true` engedélyezi; a domainengedélyezés a hozzárendelésekhez változatlanul szükséges. Már engedélyezett registry esetén nincs konfigurációs teendő. A szokásos jóváhagyott merge utáni telepítés és a gate szolgáltatás újraindítása tölti be az új kódot; ezt a PR nem hajtja végre.

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

Területválasztáskor az eszköz összes entitásának közös területe eszközszintű beállítást, az eltérő saját entitásterületek pedig öröklésre visszaállítást (`area_id:null`) indokolnak. Az agent előbb ellenőrizze az eszközmódosítás sikerét; sikertelen vagy bizonytalan kimenetelre alapozva ne töröljön saját entitásterületet. Ha csak egy entitás van máshol, csak azt kell módosítani; eszköz nélküli entitásnál szintén az entitást. Bizonytalan esetben az agent kérdezzen. Az egymás előállapotát befolyásoló eszköz- és entitásterület-lépések külön, célhoz kötött gate-jóváhagyást igényelnek; a független módosítások a fenti rögzített csomagba foghatók. Minden tag változatlan policy-ellenőrzést kap, automatikus célkiválasztás nincs.

Az elfogadott mezők kizárólag `area_id`, `labels_add`, `labels_remove`, `name`, `icon`, `hidden_by`, `disabled_by`. Ismeretlen vagy ismételt JSON-mező, hibás típus és hatás nélküli kérés elutasított. A név legfeljebb 80 karakter; az ikon legfeljebb 64 karakteres `mdi:` azonosító. A címkelisták és az eredő hozzárendelés legfeljebb 16 eleműek. Ezek rögzített implementációs határok, nem konfigurációs kulcsok. Nem auditálható, titok- vagy vezérlőkarakter-gyanús szövegnél elutasítás van, nem csendesen átírt szándék.

## Jóváhagyás, mentés és versenyhelyzet

Az egycélú jóváhagyási push a cél emberi nevét és a változó mezők teljes emberi régi/új értékét mutatja. Entitásnál mindkét ág a HA állapot `friendly_name` értékét olvassa a meglévő, engedélyezett `GET /api/states/{entity_id}` útvonalon: így a HA 2026.11 saját kompatibilitási, eredeti név- és eszközprefix-szabálya érvényesül, az ismétlődő névrészeket nem vonjuk össze. Ez entitásonként egy további olvasás; hiba esetén nincs névrejtő visszaesés. Kizárólag hiányzó vagy null megjelenítési névnél használható a konkrét kanonikus `entity_id`, a kötött céllal ellenőrizve. Jelen lévő tiltott, hibás vagy túl hosszú név elutasított, nem helyettesített. A megjelenítés nem írja át a nullable registry `name` értéket, nem része a hash-nek, és nem változtatja az előállapot-ellenőrzést. Az egycélú ág két rövid sorába nem férő változás kisebb kérésekre bontást kérő elutasítás, nem csonkított jóváhagyás; más megjelenítési hibák külön biztonságos okot kapnak. A csomagos ág teljes listája ehelyett a fent leírt persistent notificationbe kerül. Az ikonmódosítás jelenleg mindkét ágon elutasított. A részletek az auditban és a státuszútvonalon megmaradnak; a jóváhagyási hash a konkrét előállapothoz és változtatáshoz kötött. A gate még a jóváhagyási push előtt tartósan auditálja a teljes szükséges előállapotot és a kért eredményt. Ha ez nem őrizhető meg hiánytalanul és titokmentesen, nincs jóváhagyás és írás. A régi értékek kézi visszaállítás alapjai; automatikus rollback-eszköz nincs.

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
