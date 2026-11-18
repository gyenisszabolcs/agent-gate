# Korlátozott Home Assistant hibanapló-olvasás

## Használat és jogosultság

A `ha_get_logs` mindig regisztrált, explicit `Read`, read-only, nem destruktív MCP-eszköz. A közös hitelesítési, vészleállítási policy- és tartós auditkapun keresztül érhető el. Új olvasási felület érzékeny naplóadatokhoz, nem általános fájl- vagy HTTP-hozzáférés; a vészleállító az olvasást nem tiltja.

Példa `tools/call`: `{"name":"ha_get_logs","arguments":{"limit":5,"level":"ERROR","integration":"mqtt"}}`. Üres `arguments` is használható.

- `limit`: elhagyva **20**; megadva **1–100 közötti egész szám**. Nincs korlátlan érték. Explicit null, nulla, negatív, törtszám, túlcsordulás és 100 feletti szám elutasítva, titoklekérés/HTTP nélkül.
- `level`: elhagyható; pontos, nagybetűs `DEBUG`, `INFO`, `WARNING`, `ERROR` vagy `CRITICAL`. Nem minimális szint, hanem pontos egyezés.
- `integration`: elhagyható; 1–64 karakter, `[a-z][a-z0-9_]*`, pontos integrációazonosító, például `mqtt`. Nem loggerprefix, URL, regex, wildcard vagy kód. A `homeassistant.components.mqtt...` és `custom_components.mqtt...` loggerek integrációja `mqtt`; más loggernél ismeretlen (`null`).

A szűrés a gate-ben történik, a teljes bemenet titokvédelme után és a limit előtt. A legutolsó találatok a fájlbeli sorrendben, régebbitől újabbig érkeznek. Nincs időbélyeg szerinti újrarendezés, lapozás vagy több naplófájl összefésülése.

## Rögzített korlátok és kimenet

Nincs új konfigurációs kulcs, NuGet-függőség, titokforrás vagy credential. A meglévő `HomeAssistant` kliens konfigurációját és `HomeAssistant:TokenSecretId` titokhivatkozását használja. A HA végpont adminjogot kér; az alkalmatlan tokent nem helyettesítjük és jogosultságát nem bővítjük.

A számszerű korlátok kódbeli plafonok, nem konfiguráció:

- Teljes upstream válasz: a meglévő **1 MiB (1 048 576 bájt)**; túllépéskor fix hiba, nincs részleges nyers bemenet feldolgozása.
- Hívói limit: alap 20, maximum 100 bejegyzés; a méretkeret ennél kevesebbet is eredményezhet.
- A teljes szerializált MCP `CallToolResult` legfeljebb **32 000 UTF-8 bájt**. Ez konzervatív 32 KB, nem karakterszám. A `structuredContent`, a JSON-t tartalmazó `content[0].text` másolata, annak escape-jei és az összes metaadat együtt számít. A JSON-RPC/SSE/HTTP szállítási keret nem a tooleredmény része.
- A meglévő közös szövegszűrő 65 536 UTF-16 kódegység felett teljes kitakarással zár. Ezt nem kerüljük meg kisebb darabokra bontással: nagy, ártalmatlan log is teljesen elveszhet, még az 1 MiB hálózati plafon alatt.

A strukturált objektum mezői:

- `items`: elemenként `timestamp`, `level`, `integration` (string vagy `null`) és már szűrt `text`.
- `returned_count`: a ténylegesen visszaadott elemek száma, nem az összes találaté.
- `truncated`: igaz, ha a bejegyzésszám, a bájtkeret vagy a teljes kitakarás miatt tartalom maradt ki.
- `redacted`: igaz a **teljes naplóeredmény visszatartásakor**; ilyenkor `items: []`, `returned_count: 0`, `truncated: true`. Nem az egyes pontos tokenhelyettesítések számlálója.

A szöveges másolat ugyanazt az objektumot tartalmazza. Mérettúllépésnél először a legrégebbi kiválasztott bejegyzések esnek ki. Egyetlen túl nagy bejegyzésnél a már teljesen szűrt szöveg eleje marad meg; a vágás nem felez UTF-16 surrogate-párt. Ha az egyetlen bejegyzés üres szöveggel, pusztán a metaadataival sem fér a keretbe, az egész bejegyzés kimarad: `items: []`, `returned_count: 0`, `truncated: true`; a `redacted` titokvédelmi jelző változatlan marad. A csonkolásjelző és a metaadatok minden esetben a kereten belül vannak.

## Formátum és folytatósorok

A támogatott fejléc a sor elején áll: `YYYY-MM-DD HH:MM:SS.mmm+HH:MM LEVEL (thread) [logger] message`. A régebbi, offset nélküli időbélyeg is felismerhető. Csak érvényes dátum és a fenti öt szint ad metaadatot. A CRLF/CR sorvégeket LF-re normalizáljuk, az utolsó sorvég nem része a bejegyzésnek.

Új bejegyzést csak teljes, nem behúzott, felismerhető fejléc kezd. A traceback, a behúzott fejlécnek látszó sor és minden más folytatósor az előző bejegyzésben marad. A legelső felismerhető fejléc előtti szöveg egy ismeretlen bejegyzés; teljesen ismeretlen formátumnál a teljes szöveg egy ilyen elem, kitalált szint/integráció/időbélyeg nélkül. Ezek nem felelnek meg a szint-/integrációszűrőnek. A fejléc felismerése szintaktikai, nem hitelesítési bizonyíték: egy üzenetbe írt, teljesen szabályos, nem behúzott fejléc a valódi fejlécével azonos alakú lehet.

## Titokvédelem és konzervatív hibázás

A teljes UTF-8 naplószövegen a bejegyzésdarabolás és bármilyen méretvágás **előtt** kötelező a meglévő `SecretRedactor.RedactText`, valamint az aktuális, konkrét HA-token pontos helyettesítése. Hibás UTF-8 teljes elutasítás.

Ezt kizárólag ezen a logútvonalon további fail-closed guard egészíti ki. A pontos ismert token eltávolítása utáni teljes szöveg vizsgálati másolatából whitespace, vezérlő- és Unicode-format karakterek kimaradnak. Ha ebben ismert token marad (például sortöréssel szétszedve), vagy `token`, `password`, `passwd`, `secret`, `authorization`, `credential`, `cookie`, `key` részlet található kis-/nagybetűtől függetlenül, a **teljes eredmény visszatartott**. Regex-időtúllépésnél is ez történik. Nincs ismeretlen hosszúságú érzékeny értékből részleges kiadás, így a következő sorra vagy U+2028 mögé került jelszórész sem marad meg.

Ez tudatos túltiltás: szabályos, egyébként szűrhető hozzárendelés, ártalmatlan „token expired”, „keyboard”, vagy whitespace eltávolításakor összeálló jelölő is kiürítheti a teljes eredményt. Ilyenkor szűkebb hívói szűrő vagy limit sem kerüli meg a védelmet, mert az egész bemenet vizsgálata megelőzi ezeket.

A közös redactor nem változott és nem vált általános DLP-vé. Jelöletlen, ismeretlen nevű vagy kódolt/obfuszkált ismeretlen titkok szemantikus felismerése továbbra sem garantált. A guard nem dekódol URL-t, base64-et vagy Unicode-escape-eket. Az engedélyezett naplóforrás és az integrációk által naplózott adat továbbra is bizalmi határ; ez nem állítás egy éles incidens teljes okáról.

A `limit`, `level`, `integration` a közös szűrés szerint auditálható bemenet. Sem a naplóeredmény, sem a nyers szöveg nem auditparaméter, és a modul nem naplózza ezeket. A provider üzenete, választeste és kivétel-inputja nem kerül a hibába; a toolhiba fix „A Home Assistant naplólekérés sikertelen.”, hiányzó kliensnél „A Home Assistant nincs beállítva.”. A hívói megszakítás titokmentesen továbbadódik.

## Transport és ellenőrzött elsődleges forrás

Kizárólag `GET /api/error_log`; nincs hívói URL/fájlnév/sablon. Ugyanaz a kliens őrzi a validált origint, a futásidejű tokent, a redirect/proxy/cookie tilalmat és az egyetlen, titoklekéréstől a teljes választestig érvényes határidőt. Az általános HA JSON- és vezérlési útvonalak változatlanok.

A Home Assistant **2026.11.0** rögzített upstream forrásával ellenőrizve:

- [APIErrorLog](https://github.com/home-assistant/core/blob/2026.11.0/homeassistant/components/api/__init__.py#L526-L538): `require_admin`, `web.FileResponse`, nem JSON.
- [Végpontkonstans](https://github.com/home-assistant/core/blob/2026.11.0/homeassistant/const.py#L988): `/api/error_log`.
- [LOG_FORMAT és HomeAssistantLogFormatter](https://github.com/home-assistant/core/blob/2026.11.0/homeassistant/util/logging.py#L24-L38): időbélyeg, szint, szál, logger, üzenet; millimásodperc és helyi UTC-offset.

Éles rendszer elérése helyett az ellenőrzések mockolt HA-transportot és a valódi helyi MCP-hostot használják.
