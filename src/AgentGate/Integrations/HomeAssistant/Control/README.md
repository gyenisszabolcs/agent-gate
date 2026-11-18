# Home Assistant vezérlés

A `ha_control` fix `Control`, a `ha_sensitive_control` fix `SensitiveControl` besorolású MCP-eszköz. Mindig regisztráltak; a `HomeAssistantControl` szakasz teljes hiánya biztonságos toolhibát ad új HTTP-, titoklekérés és jóváhagyási push nélkül. A külön beállított olvasási kliens, jóváhagyási csatorna és titokönteszt önálló marad. Jelenlevő üres/null/skalár vagy hibás vezérlési szakasz indulási hiba, nem kikapcsolás. Bekapcsolt vezérléshez érvényes `HomeAssistant` klienskonfiguráció szükséges.

## Hívás és jóváhagyás

Felolvasáshoz (`media_player` / `speak`) először a `ha_control` eszközt hívd; csendes órákon kívül ez a megfelelő út. Csak akkor válaszd a `ha_sensitive_control` jóváhagyási útját felolvasásra, ha a gate kifejezetten a csendes órákra hivatkozva utasította el a normál kérést. Az időablakot és a jogosultságot a gate dönti el, nem az agent saját órája. Policy-, entitás-, hangerő-, szöveg-, kvóta- vagy hálózati hiba nem indok sensitive újrapróbálásra. Ez a hívási útmutató nem változtatja meg a sensitive zár/cover vezérlést vagy a futásidejű jóváhagyási szabályokat.

Mindkét eszköz argumentumai: `domain`, `service`, `entity_id`, opcionálisan `data`. Példa:

```json
{"name":"ha_control","arguments":{"domain":"light","service":"turn_on","entity_id":"light.nappali","data":{"brightness_pct":50}}}
```

Pontosan egy kisbetűs, kanonikus `domain.object_id` cél adható meg, a külön domainnel egyezően. Nincs `all`, wildcard, felsorolás, tömb, area/device/target vagy idegen felső argumentum; az utóbbit a közös kapu még az SDK paraméterkötése előtt elutasítja. A `data` hiánya és top-level null üres adat; egyébként kizárólag lapos, ismétlődésmentes JSON-objektum. Nincs ismeretlen mező, mezőnkénti null, string→szám konverzió, kerekítés vagy clamp. A cél és az egység nem írható felül a `data` mezőből.

A pontos összehasonlítást igénylő `brightness_pct`, `temperature`, valamint a friss HA-állapotból származó `min_temp` és `max_temp` JSON-számtokenje legfeljebb **4096 ASCII karakter** lehet (előjel, tizedespont és exponentjelölés együtt). Ez fix, nem konfigurálható lexikális erőforráskorlát, amelyet a double/BigInteger feldolgozás előtt ellenőrzünk. A hosszabb szám biztonságos elutasítást és nulla POST-ot eredményez; nincs kerekítés, clamp vagy konverzió.

A sensitive kérés a meglévő [aszinkron jóváhagyási kapun](../../../Approval/README.md) fut. Konfigurált és elérhető [HA-push csatorna](../../../Approval/HomeAssistant/README.md) szükséges; a kezdeti válasz `jóváhagyásra vár: <azonosító>`. Az eredmény a `gate_approval_status` és a meglévő eredményértesítés útján érhető el. A teljes normalizált payload, tool, kockázat és tulajdonos változtathatatlan hash-kötést kap. A jóváhagyás egyszer használható, lejár, nem általános entitásengedély. Nincs automatikus toolváltás vagy kockázatcsökkentés.

## Kódbeli felső korlát

A konfiguráció csak szűkítheti az alábbi mátrixot. Ahol nincs megengedett adat, a nem üres `data` hiba.

| Tool / domain | Szolgáltatások | Megengedett data |
| --- | --- | --- |
| Control / light | `turn_on`, `turn_off`, `toggle` | Csak turn_on/toggle: opcionális `brightness_pct` JSON-szám 0–100 és `color_temp_kelvin` JSON-egész 2000–6500. |
| Control / switch | `turn_on`, `turn_off`, `toggle` | Nincs. Csak explicit entitáslista. |
| Control / media_player | `turn_on`, `turn_off`, `media_play`, `media_pause`, `media_stop`, `media_next_track`, `media_previous_track`, `volume_mute`, `volume_set`, `speak` | Csak explicit entitáslista. volume_mute: kötelező bool `is_volume_muted`; volume_set: kötelező szám `volume_level` 0 és MediaPlayerMaxVolume között. speak: lásd lent. |
| SensitiveControl / media_player | `speak` | Ugyanaz a felolvasás, mindig jóváhagyással. Más médiavezérlés ezen az úton nincs. |
| Control / cover | `open_cover`, `close_cover`, `stop_cover`, `set_cover_position` | Csak set_cover_position: kötelező `position` JSON-egész 0–100. |
| Control / climate | `set_temperature`, `set_hvac_mode`, `turn_on`, `turn_off` | set_temperature: kötelező `temperature` JSON-szám 10–30. set_hvac_mode: kötelező `hvac_mode` a konfigurált listából. |
| Control / scene | `turn_on` | Nincs. |
| Control / input_boolean | `turn_on`, `turn_off`, `toggle` | Nincs. |
| SensitiveControl / lock | `lock`, `unlock` | Nincs, PIN/code sem. |
| SensitiveControl / cover | `open_cover`, `close_cover`, `stop_cover` | Nincs; set_cover_position tiltott. |

Riasztó, konfigurációírás és minden más domain/szolgáltatás tiltott. Normál cover csak `awning`, `blind`, `curtain`, `damper`, `shade`, `shutter`, `window`; érzékeny cover csak `garage`, `gate`, `door`. A device_class kizárólag friss, célazonos egyedi GET-ből jöhet: hiányzó/null/nem string/ismeretlen osztály vagy hibás/elérhetetlen állapot tilt. Sensitive esetben a push előtt aszinkron előkészítés, minden cover-POST előtt új GET történik; a közben megváltozott osztály nem ad újrahasznosítható engedélyt.

### Celsius, konverzió nélkül

A set_temperature közvetlen előellenőrzése friss egyedi állapotból olvassa a kötelező, véges JSON-szám `min_temp` és `max_temp` attribútumokat; min ≤ max szükséges. A kérésnek a zárt `[max(10,min_temp), min(30,max_temp)]` °C intervallumba kell esnie. Egypontos metszet pontos értékkel megengedett; üres metszet, hibás/hiányzó határ vagy kívül eső érték tilt.

A jelenlevő `temperature_unit` és/vagy `unit_of_measurement` attribútum mindegyikének pontosan `°C` stringnek kell lennie. Jelenlevő hibás, más egységű vagy ellentmondó értéket globális beállítás nem írhat felül. Csak mindkét entitásegység hiányakor igazolhatja friss `/api/config` GET `unit_system.temperature` mezője a `°C` egységet. Sok climate entitás a HA globális kijelzési egységére támaszkodik; az egység hiánya nem jelent automatikusan Celsiust. Nincs default, átváltás vagy caller által megadott egység/határ. Ezek a feltételek nem a climate turn_on/turn_off vagy set_hvac_mode hívására vonatkoznak.

### Hangszóró és szövegfelolvasás

A `speak` a gate szűk műveletneve, nem közvetlen media_player szolgáltatás. Példák:

```json
{"name":"ha_control","arguments":{"domain":"media_player","service":"volume_set","entity_id":"media_player.nappali","data":{"volume_level":0.4}}}
{"name":"ha_control","arguments":{"domain":"media_player","service":"speak","entity_id":"media_player.nappali","data":{"message":"Elkészült a mosás.","volume_level":0.4}}}
```

A `play_media` még a kódbeli felső korlátban sem szerepel. Nincs tetszőleges URL-, fájl- vagy tartalomlejátszás. `media_player` esetén az `AllowAllEntities=true` indulási hiba; kizárólag az `EntityAllowlist` pontos nevei jogosítanak, a read-domainengedéllyel és denylisttel metszve. A hangerő összehasonlítása pontos decimális, a meglévő 4096 karakteres számtokenkorláttal; túllépés elutasítás, nem clamp.

A felolvasás kötelező `data.message` mezője nem üres, legfeljebb 200 UTF-16 kódegység hosszú szöveg. Engedélyezett: Unicode betűk, ékezetek, számok, szóköz és a következő központozás: `. , ! ? ; : ' " ( ) = + % - [ ]`. Nincs sortörés/vezérlőkarakter, SSML/HTML, sablon, URL-séma vagy tartalomút (`/`, `\\`, `<`, `>`, `{`, `}` tiltott). A meglévő heurisztikus titokszűrő a ténylegesen továbbított szövegre is lefut; ha a szűrt változat 200 kódegységnél hosszabb, elutasítás. A szűrő nem általános titokfelismerő: korlátai a [titokszűrő leírásában](../../../../../docs/titokszuro.md) szerepelnek. Felismerhetetlen, jelöletlen titkot ne küldj szövegként.

A `data.volume_level` opcionális, hiányakor a konfigurált maximum kerül a normalizált, jóváhagyáshoz kötött kérésbe. Nem jelent korlátlan korábbi hangerő használatát. Minden TTS először `media_player.volume_set` hívást végez, majd célzott friss GET-tel ellenőrzi, hogy a hangerő véges, nem negatív és nem nagyobb sem a kért értéknél, sem a konfigurált plafonnál. Hiányzó, túl magas, duplikált, hibás vagy unknown/unavailable állapotnál nincs beszéd. A hangerőt nem állítjuk vissza. A TTS-jog a művelethez szükséges belső hangerőállítást is magában foglalja; nem szükséges külön caller `volume_set` jogosultság.

Ezután kizárólag a konfigurált `Tts:EngineEntityId` motor kap `tts.speak` hívást a rögzített `media_player_entity_id`, szűrt `message` és `cache=false` adatokkal. A hívó motort, nyelvet, options/cache mezőt, URL-t vagy más célt nem választhat. A motor, cél, szűrt szöveg és hangerő a sensitive előkészítéskor hash-kötést kap; a motor a jóváhagyási összefoglalóban is megjelenik. Az audit a szűrt szöveget és az eredeti bemenet hosszát (`data.text_length`) tárolja, nyers szöveget nem.

A csendes időablak kezdete zárt, vége nyílt: alapból `[22:00,07:00)` Europe/Budapest. Az aktuális UTC időt alakítjuk helyi faliórára; az őszi ismételt és tavaszi átugrott időpontok is ugyanazt a helyi szabályt követik. Napon belüli ablak is megadható; azonos kezdés/vég indulási hiba, nem rejtett kikapcsolás. A `ha_control` csendes órákban elutasít és a `ha_sensitive_control` használatát nevezi meg. Nincs automatikus útváltás. A sensitive út nappal is mindig jóváhagyást kér. A végrehajtás és mindkét POST előtt újbóli idő-, policy- és vészleállító-ellenőrzés történik, nem csupán a tool metódusban.

A két út és minden hangszóró közös, fix 3 TTS-kísérlet / gördülő 60 másodperc limitet használ, monoton órával és atomikus foglalással. A pending jóváhagyási kérés nem foglal; a kvótát a jóváhagyott végrehajtó sem kerülheti meg. A végrehajtás a hangerőállítás előtt lefoglal egy helyet, amely az előkészítés alatt nem járhat le. A gördülő 60 másodperc a tényleges beszéd-POST előtti utolsó ellenőrzéstől (vagy korábbi hiba esetén a kísérlet végétől) indul; a lassú előkészítés sem sűríthet össze később több beszédet. A TTS az általános entitás/globális vezérlési kvótát is fogyasztja (egy logikai kísérlet, nem két külön hely a két POST miatt). A TTS-hely konzervatívan akkor is elfogyhat, ha az általános kvóta később elutasít. Hibás hangerőállítás/visszaolvasás/beszéd is fogyaszt; nincs várakozó retry. A számlálók folyamaton belüliek, restartkor nullázódnak; egy gate-példányra tervezve.

Az összes gate-médiaművelet közös aszinkron végrehajtási zárat használ, így másik gate-hívás nem emelhet közben hangerőt. A HA és fizikai eszköz nem tranzakcionális: külső automatizáció, csoporttag, fizikai vezérlő vagy más HA-kliens továbbra is változtathat az állapoton. Ezek a meglévő HA-oldali bizalmi határ részei; nem ígérünk hardveres hangerőkorlátozást. A késleltetett hangerő-visszajelzés biztonságos elutasítást okozhat. A sikeres TTS-válasz csak az elküldést jelzi, nem a felolvasás befejezését. Részleges hiba után a hangerő már megváltozhatott, beszéd is történhetett; nincs rollback vagy újrapróbálás.

Elsődleges API-leírások: [TTS speak](https://www.home-assistant.io/actions/tts.speak/), [hangerőállítás](https://www.home-assistant.io/actions/media_player.volume_set/), [némítás](https://www.home-assistant.io/actions/media_player.volume_mute/), [médialejátszó műveletek](https://www.home-assistant.io/integrations/media_player/). A gate az upstreamnél szándékosan szűkebb sémát kínál.

## Konfiguráció

A [teljes, a végleges parserekkel és startup-validatorokkal tesztelt példa](appsettings.example.jsonc) az olvasási klienshez és a már meglévő push csatornához kapcsolódik. A GUID és minden név fiktív; nincs benne titokérték. A régi, 12 domaint tartalmazó olvasási példa változatlan. Az új példában a `HomeAssistant:DomainAllowlist` kifejezetten `lock`-ot is tartalmaz: a meglévő HA-domainengedély és a vezérlési policy mindig metszet. A read-domainengedély önmagában nem keletkeztet írási jogot.

Minden új kulcs:

| Pontos kulcs | Szerep / alapérték |
| --- | --- |
| `HomeAssistantControl` | Opcionális vezérlési objektum; teljes hiánya kikapcsolás. |
| `HomeAssistantControl:Domains` | Engedélyezett domainek objektuma; hiányzó/üres domain nem enged semmit. |
| `HomeAssistantControl:Domains:<domain>:AllowAllEntities` | Boolean, alapból false; switch és media_player esetén true mindig hiba. |
| `HomeAssistantControl:Domains:<domain>:EntityAllowlist` | Pontos entity_id stringtömb, alapból üres. AllowAllEntities=true mellett nem lehet nem üres. |
| `HomeAssistantControl:Domains:<domain>:ControlServices` | Control-mátrix részhalmaza, stringtömb, alapból üres. |
| `HomeAssistantControl:Domains:<domain>:SensitiveControlServices` | SensitiveControl-mátrix részhalmaza, stringtömb, alapból üres. |
| `HomeAssistantControl:EntityDenylist` | Globális pontos entity_id stringtömb, alapból üres; mindkét tool minden engedélyét felülírja. |
| `HomeAssistantControl:HvacModeAllowlist` | Stringtömb; ismert felső korlát: off, heat, cool, heat_cool, auto, dry, fan_only. Alapból üres; a mintában off/heat/cool/auto. Üresen set_hvac_mode tiltott. |
| `HomeAssistantControl:RateLimit` | Gördülő írási kvóta objektuma. |
| `HomeAssistantControl:RateLimit:PerEntityPerMinute` | Pozitív Int32, alapból és a mintában 6. Null/hibás/túlcsorduló érték startuphiba. |
| `HomeAssistantControl:RateLimit:GlobalPerMinute` | Pozitív Int32, alapból és a mintában 60. Null/hibás/túlcsorduló érték startuphiba. |
| `HomeAssistantControl:MediaPlayerMaxVolume` | JSON-szám 0–1, alapból 0.6; média- és TTS-hangerő közös maximuma. |
| `HomeAssistantControl:Tts` | Opcionális, szigorúan validált TTS-konfigurációs objektum; jelenlevő üres/null/hibás objektum startuphiba. |
| `HomeAssistantControl:Tts:EngineEntityId` | Pontos `tts.*` entitásnév; nincs alapérték. Ha bármelyik út engedélyezi a speak műveletet, kötelező. Nem titok, nem caller-argumentum. |
| `HomeAssistantControl:Tts:QuietHoursStart` | `HH:mm`, alapból `22:00`, a csendes időablak zárt kezdete. |
| `HomeAssistantControl:Tts:QuietHoursEnd` | `HH:mm`, alapból `07:00`, a csendes időablak nyílt vége. |
| `HomeAssistantControl:Tts:TimeZone` | Rendszer által ismert időzóna, alapból `Europe/Budapest`; hibás/ismeretlen név startuphiba. |

A mintában light/cover/climate/scene/input_boolean/lock minden entitása engedélyezett a felsorolt szolgáltatásokra; a switch explicit listája üres, így egyetlen switch sem vezérelhető. Ezek mintaértékek, nem automatikusan bekapcsolt éles jogok. A policy induláskor rögzül; konfigurációváltás érvényesítéséhez újraindítás szükséges, a folyamatban levő jóváhagyások nem élnek túl restartot. A végrehajtás minden POST előtt ismét ellenőrzi a policyt és a vészleállítót. Új NuGet vagy credentialforrás nincs. A már meglévő `HomeAssistant:*`, `HomeAssistantApproval:*`, `Approval:*`, `Audit:*` és `KillSwitch:*` kulcsok jelentése nem változik.

## Végrehajtási és bizalmi határ

- Tartós előzetes audit nélkül nincs végrehajtás. Az audit csak domain/service/entity és az adott művelethez teljesen validált, lapos `data.<mező>` skalárokat tartalmaz; tetszőleges JSON-objektumot továbbra sem naplóz. Hibás data csak kihagyásjelölőt hagy, nyers kulcs/érték nélkül.
- Közvetlenül a POST előtt új policy- és kill-switch-ellenőrzés történik a titoklekérés és friss állapotellenőrzés után. A sensitive kapu saját végrehajtási ellenőrzései is megmaradnak.
- Mindkét tool és az approved worker ugyanazt az atomikus limitert használja. Monoton, gördülő 60 másodperc, egy lépésben entitás+globál foglalás; elutasítás nem fogyaszt részkeretet. A pending várakozás nem foglal. Indított hibás POST is fogyaszt; nincs retry/autowait. A lejárt bejegyzések a következő próbánál törlődnek, az entitásmetaadat is; a konfigurációtól független 65536 aktív próbás memóriaplafon telítve elutasít.
- Normál vezérlésnél egy ellenőrzött `/api/services/<domain>/<service>` POST megy összeállított JSON-törzzsel, egyetlen entity_id-val. TTS-nél a fent leírt két szűk POST és köztes hangerő-visszaolvasás történik. A meglévő origin/HTTPS vagy explicit privát HTTP, redirect/proxy/cookie, token-, méret-, mélység-, timeout- és cancellation-védelem megmarad.
- A nyers POST-válasz soha nem kerül MCP-/státusz-/push eredménybe: más entitások adata is lehet benne. Normál vezérlés után célzott GET és a meglévő 2.7 IsSafe/State/Attributes projekció adja az állapotot. Ez az akkor látható HA-állapot, nem a fizikai mozgás befejezésének ígérete. TTS-nél csak a szűk elküldési visszaigazolás tér vissza, a felolvasott szöveg nélkül.
- Ha a POST elindult, de válasz/visszaolvasás/audit hibázik, a hatás megtörténhetett. Nincs rollback-ígéret és nincs ismétlés, különösen toggle/unlock esetén sem. A megszakítás nem bizonyítja a fizikai hatás elmaradását.
- A GET és POST nem atomikus. A HA device_class és állapot hitelessége, a HA-oldali automatizációk és csoportok az üzemeltető bizalmi határán belül vannak. Egy entity_id több fizikai eszközre is hathat, különösen scene vagy csoport esetén. A gate nem módosít HA-jogosultságot vagy éles rendszert.
