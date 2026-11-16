# Home Assistant vezérlés

A `ha_control` fix `Control`, a `ha_sensitive_control` fix `SensitiveControl` besorolású MCP-eszköz. Mindig regisztráltak; a `HomeAssistantControl` szakasz teljes hiánya biztonságos toolhibát ad új HTTP-, titoklekérés és jóváhagyási push nélkül. A külön beállított olvasási kliens, jóváhagyási csatorna és titokönteszt önálló marad. Jelenlevő üres/null/skalár vagy hibás vezérlési szakasz indulási hiba, nem kikapcsolás. Bekapcsolt vezérléshez érvényes `HomeAssistant` klienskonfiguráció szükséges.

## Hívás és jóváhagyás

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

## Konfiguráció

A [teljes, a végleges parserekkel és startup-validatorokkal tesztelt példa](appsettings.example.jsonc) az olvasási klienshez és a már meglévő push csatornához kapcsolódik. A GUID és minden név fiktív; nincs benne titokérték. A régi, 12 domaint tartalmazó olvasási példa változatlan. Az új példában a `HomeAssistant:DomainAllowlist` kifejezetten `lock`-ot is tartalmaz: a meglévő HA-domainengedély és a vezérlési policy mindig metszet. A read-domainengedély önmagában nem keletkeztet írási jogot.

Minden új kulcs:

| Pontos kulcs | Szerep / alapérték |
| --- | --- |
| `HomeAssistantControl` | Opcionális vezérlési objektum; teljes hiánya kikapcsolás. |
| `HomeAssistantControl:Domains` | Engedélyezett domainek objektuma; hiányzó/üres domain nem enged semmit. |
| `HomeAssistantControl:Domains:<domain>:AllowAllEntities` | Boolean, alapból false; switch esetén true mindig hiba. |
| `HomeAssistantControl:Domains:<domain>:EntityAllowlist` | Pontos entity_id stringtömb, alapból üres. AllowAllEntities=true mellett nem lehet nem üres. |
| `HomeAssistantControl:Domains:<domain>:ControlServices` | Control-mátrix részhalmaza, stringtömb, alapból üres. |
| `HomeAssistantControl:Domains:<domain>:SensitiveControlServices` | SensitiveControl-mátrix részhalmaza, stringtömb, alapból üres. |
| `HomeAssistantControl:EntityDenylist` | Globális pontos entity_id stringtömb, alapból üres; mindkét tool minden engedélyét felülírja. |
| `HomeAssistantControl:HvacModeAllowlist` | Stringtömb; ismert felső korlát: off, heat, cool, heat_cool, auto, dry, fan_only. Alapból üres; a mintában off/heat/cool/auto. Üresen set_hvac_mode tiltott. |
| `HomeAssistantControl:RateLimit` | Gördülő írási kvóta objektuma. |
| `HomeAssistantControl:RateLimit:PerEntityPerMinute` | Pozitív Int32, alapból és a mintában 6. Null/hibás/túlcsorduló érték startuphiba. |
| `HomeAssistantControl:RateLimit:GlobalPerMinute` | Pozitív Int32, alapból és a mintában 60. Null/hibás/túlcsorduló érték startuphiba. |

A mintában light/cover/climate/scene/input_boolean/lock minden entitása engedélyezett a felsorolt szolgáltatásokra; a switch explicit listája üres, így egyetlen switch sem vezérelhető. Ezek mintaértékek, nem automatikusan bekapcsolt éles jogok. A policy induláskor rögzül; konfigurációváltás érvényesítéséhez újraindítás szükséges, a folyamatban levő jóváhagyások nem élnek túl restartot. A végrehajtás minden POST előtt ismét ellenőrzi a policyt és a vészleállítót. Új NuGet vagy credentialforrás nincs. A már meglévő `HomeAssistant:*`, `HomeAssistantApproval:*`, `Approval:*`, `Audit:*` és `KillSwitch:*` kulcsok jelentése nem változik.

## Végrehajtási és bizalmi határ

- Tartós előzetes audit nélkül nincs végrehajtás. Az audit csak domain/service/entity és az adott művelethez teljesen validált, lapos `data.<mező>` skalárokat tartalmaz; tetszőleges JSON-objektumot továbbra sem naplóz. Hibás data csak kihagyásjelölőt hagy, nyers kulcs/érték nélkül.
- Közvetlenül a POST előtt új policy- és kill-switch-ellenőrzés történik a titoklekérés és friss állapotellenőrzés után. A sensitive kapu saját végrehajtási ellenőrzései is megmaradnak.
- Mindkét tool és az approved worker ugyanazt az atomikus limitert használja. Monoton, gördülő 60 másodperc, egy lépésben entitás+globál foglalás; elutasítás nem fogyaszt részkeretet. A pending várakozás nem foglal. Indított hibás POST is fogyaszt; nincs retry/autowait. A lejárt bejegyzések a következő próbánál törlődnek, az entitásmetaadat is; a konfigurációtól független 65536 aktív próbás memóriaplafon telítve elutasít.
- Egy ellenőrzött `/api/services/<domain>/<service>` POST megy összeállított JSON-törzzsel, egyetlen entity_id-val. A meglévő origin/HTTPS vagy explicit privát HTTP, redirect/proxy/cookie, token-, méret-, mélység-, timeout- és cancellation-védelem megmarad.
- A nyers POST-válasz soha nem kerül MCP-/státusz-/push eredménybe: más entitások adata is lehet benne. Utána célzott GET és a meglévő 2.7 IsSafe/State/Attributes projekció adja az állapotot. Ez az akkor látható HA-állapot, nem a fizikai mozgás befejezésének ígérete.
- Ha a POST elindult, de válasz/visszaolvasás/audit hibázik, a hatás megtörténhetett. Nincs rollback-ígéret és nincs ismétlés, különösen toggle/unlock esetén sem. A megszakítás nem bizonyítja a fizikai hatás elmaradását.
- A GET és POST nem atomikus. A HA device_class és állapot hitelessége, a HA-oldali automatizációk és csoportok az üzemeltető bizalmi határán belül vannak. Egy entity_id több fizikai eszközre is hathat, különösen scene vagy csoport esetén. A gate nem módosít HA-jogosultságot vagy éles rendszert.
