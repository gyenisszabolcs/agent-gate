# Korlátozott Home Assistant rendszernapló-olvasás

## Használat és jogosultság

A `ha_get_logs` mindig regisztrált, explicit `Read`, read-only, nem destruktív MCP-eszköz. A közös hitelesítési, vészleállítási policy- és tartós auditkapun keresztül érhető el. Új olvasási felület érzékeny naplóadatokhoz, nem általános fájl- vagy HTTP-hozzáférés; a vészleállító az olvasást nem tiltja.

Példa `tools/call`: `{"name":"ha_get_logs","arguments":{"limit":5,"level":"ERROR","integration":"mqtt"}}`. Üres `arguments` is használható.

- `limit`: elhagyva **20**; megadva **1–100 közötti egész szám**. Nincs korlátlan érték. Explicit null, nulla, negatív, törtszám, túlcsordulás és 100 feletti szám elutasítva, titoklekérés/WS-kapcsolat nélkül.
- `level`: elhagyható; pontos, nagybetűs `WARNING`, `ERROR` vagy `CRITICAL`. Nem minimális szint, hanem pontos egyezés. Változás a korábbi szöveges forráshoz képest: `DEBUG` és `INFO` most elutasított, mert a rendszernapló nem szolgáltatja ezeket.
- `integration`: elhagyható; 1–64 karakter, `[a-z][a-z0-9_]*`, pontos integrációazonosító, például `mqtt`. Nem loggerprefix, URL, regex, wildcard vagy kód. A `homeassistant.components.mqtt...` és `custom_components.mqtt...` loggerek integrációja `mqtt`; más loggernél ismeretlen (`null`).

A sorrend: teljes válasz sémaellenőrzése; pontos szint-/integrációszűrés; minden találat teljes bejegyzésszintű titokvédelme; limit; bájtkeret. A `withheld_count` csak a szűrőnek megfelelő, titokvédelem miatt visszatartott bejegyzéseket számolja, még a limit előtt. Nem tartalmazza a szűrő, limit vagy bájtkeret miatt kimaradt elemeket. A Core LIFO forrásából a legújabb biztonságos találatokat választjuk, és a forrássorrendet megfordítva adjuk vissza (régebbitől újabbig). Nem rendezünk újra időbélyeg szerint.

## Rögzített korlátok és kimenet

Nincs új konfigurációs kulcs, NuGet-függőség, titokforrás vagy credential. A meglévő `HomeAssistant` kliens konfigurációját és `HomeAssistant:TokenSecretId` titokhivatkozását használja. A HA végpont adminjogot kér; az alkalmatlan tokent nem helyettesítjük és jogosultságát nem bővítjük.

A számszerű korlátok kódbeli plafonok, nem konfiguráció:

- Upstream eredményüzenet: **1 MiB (1 048 576 bájt)**, auth-üzenetenként 4096 bájt; teljes fogadott adat legfeljebb 1 MiB + 8192 bájt. Pontosan két auth-üzenet és egy korrelált eredmény; üzenetenként legfeljebb 256 fragment (az üres fragment is számít). Túllépéskor fix hiba, nincs részleges nyers bemenet kiadása.
- JSON-mélység legfeljebb 16; duplikált objektumkulcs, hibás UTF-8, hibás típus, hiányzó kötelező mező, nem véges/negatív időpont, elsőnél korábbi utolsó időpont vagy nem pozitív egész számláló elutasított. Legfeljebb 1024 összevont bejegyzés és bejegyzésenként 1–5 üzenet.
- Hívói limit: alap 20, maximum 100 bejegyzés; a méretkeret ennél kevesebbet is eredményezhet.
- A teljes szerializált MCP `CallToolResult` legfeljebb **32 000 UTF-8 bájt**. Ez konzervatív 32 KB, nem karakterszám. A `structuredContent`, a JSON-t tartalmazó `content[0].text` másolata, annak escape-jei és az összes metaadat együtt számít. A JSON-RPC/SSE/HTTP szállítási keret nem a tooleredmény része.
- A meglévő közös szövegszűrő 65 536 UTF-16 kódegység felett teljes kitakarással zár. Ezt nem kerüljük meg kisebb darabokra bontással: egy túl nagy, ártalmatlan bejegyzés is visszatartott lehet; biztonságos társai megmaradnak.

A strukturált objektum mezői:

- `items`: elemenként `logger`, `level`, `integration` (string vagy `null`), `messages` (stringlista), `count` (pozitív egész előfordulásszám), `first_occurred` és `timestamp` (Unix-másodperc számként), `exception` (string, üres is lehet). Nincs régi, fejlécből visszafejtett `text`; a traceback a kivételmező része. Az ismeretlen upstream mezők (például `source`) nem kerülnek a kimenetbe.
- `returned_count`: a ténylegesen visszaadott elemek száma, nem az összes találaté.
- `withheld_count`: a fenti sorrend szerint titokvédelem miatt visszatartott bejegyzések száma. Nem vész el a metadata-only fallbackben sem.
- `truncated`: igaz, ha a bejegyzésszám, a bájtkeret vagy a titokvédelmi visszatartás miatt tartalom maradt ki.
- `redacted`: igaz, ha legalább egy találat visszatartott vagy szövege módosult a titokszűrésben. A pontos tokenhelyettesítés önmagában nem növeli a `withheld_count` értékét.

A szöveges másolat ugyanazt az objektumot tartalmazza. Mérettúllépésnél először a legrégebbi kiválasztott bejegyzések esnek ki. Egyetlen túl nagy bejegyzésnél a már teljesen szűrt üzenetek, majd a kivétel közös karakterkeretének eleje marad meg; a vágás nem felez UTF-16 surrogate-párt. Ha a rekord üres üzenetekkel/kivétellel, pusztán a metaadataival sem fér a keretbe, az egész bejegyzés kimarad. A jelzők és számlálók változatlanok, a kereten belül maradnak. A gazdagabb strukturált rekordok miatt a bájtkeret 100-nál kisebb elemszámot is eredményezhet.

## Titokvédelem és konzervatív hibázás

A teljes dekódolt bejegyzésen, minden kiadható szöveges mezőjén, bármilyen méretvágás **előtt** kötelező a meglévő `SecretRedactor.RedactText`, valamint az aktuális, konkrét HA-token pontos helyettesítése. A közös szűrő nem változott. Mezőkön átívelő, egyértelműen nem rekonstruálható szűrés esetén a rekord visszatartott.

Ezt kizárólag ezen a logútvonalon fail-closed guard egészíti ki. A `token`, `password`, `passwd`, `secret`, `authorization`, `credential(s)`, `cookie`, `Bearer`, `api_key`/`api-key`/`apikey`, `access_token`/`access-token`/`accesstoken` önálló szó vagy aláhúzással határolt névrész (például `client_secret`), valamint a `key` utáni `:` vagy `=` esetén a bejegyzés visszatartott; nem találgatjuk a több soros érték végét. Regex-időtúllépésnél is ez történik. `KeyError`, `keyboard` vagy `tokenizer` önmagában nem érzékeny.

Csak a konkrét ismert token külön vizsgálati másolatából távolítunk el whitespace, vezérlő- és Unicode-format karaktereket. Ebben a forrás logger/üzenetek/kivétel mezői elválasztás nélkül összefűzve szerepelnek, így az üzenet-/mezőhatáron széttört ismert token is visszatartást okoz. A származtatott integráció és a kötött szint nem szakíthatja meg ezt az illesztést. A többi jelölőre nincs általános szóköz-összenyomás vagy részszótiltás. A visszatartás csak az adott rekordot érinti.

Ez nem általános DLP. Jelöletlen, ismeretlen nevű vagy kódolt/obfuszkált ismeretlen titkok szemantikus felismerése továbbra sem garantált. A JSON escape-eket a JSON-parser dekódolja; ezen túl nincs URL/base64 dekóder. A naplóforrás továbbra is bizalmi határ, nem állítunk bizonyított éles incidensokot.

A `limit`, `level`, `integration` a közös szűrés szerint auditálható bemenet. Sem a naplóeredmény, sem a nyers szöveg nem auditparaméter, és a modul nem naplózza ezeket. A provider üzenete, választeste és kivétel-inputja nem kerül a hibába; a toolhiba fix „A Home Assistant naplólekérés sikertelen.”, hiányzó kliensnél „A Home Assistant nincs beállítva.”. A hívói megszakítás titokmentesen továbbadódik.

## Transport és ellenőrzött elsődleges forrás

Kizárólag `/api/websocket`, egyszeri `system_log/list` (`id: 1`), nincs subscription vagy HTTP-fallback. A validált HTTPS-originből WSS, csak a meglévő privát HTTP-optin esetén WS cél lesz, ugyanazzal a porttal. Redirect/proxy/cookie tiltott. Az `auth_required` → `auth` → `auth_ok` sorrend kötelező; csak a saját azonosítójú, sikeres `result` fogadható el. Hibánál nincs reconnect/retry. A cache-elt `SecretLease` használatos; `auth_invalid` csak a ténylegesen elutasított generációt érvényteleníti. Nincs új titokforrás vagy jog. Egy határidő fedi a titoklekérést, connect/send/receive/feldolgozást. Minden művelet megvárt; a socket végül abort/dispose, nincs függő close-handshake vagy háttér-receive. Az általános HA JSON- és vezérlési útvonalak változatlanok.

A Home Assistant Core **3df93331719fcb206ab24c165cadf1e796d52c02** rögzített upstream forrásával ellenőrizve. Ez nem az aktuális vagy telepített HA-verzió állítása:

- [system_log](https://github.com/home-assistant/core/blob/3df93331719fcb206ab24c165cadf1e796d52c02/homeassistant/components/system_log/__init__.py): `LogEntry.to_dict` mezői, legfeljebb öt üzenet, `DedupStore.to_list` LIFO, WARNING küszöb és adminigényes `system_log/list`.
- [régi APIErrorLog](https://github.com/home-assistant/core/blob/3df93331719fcb206ab24c165cadf1e796d52c02/homeassistant/components/api/__init__.py): szintén `require_admin`; a vizsgált forrásban nincs új adminjogigény a régi olvasási úthoz képest.

Éles rendszer elérése helyett az ellenőrzések mockolt HA-transportot és a valódi helyi MCP-hostot használják.
