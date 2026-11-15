# Home Assistant jóváhagyási csatorna

A #30 a #29 [aszinkron kapujának](../README.md) opcionális csatornája. Nem új engedélytár és nem általános HA-szolgáltatáshívó: csak a konfigurált notify szolgáltatásnak küld, majd ellenőrzött döntést ad a kapunak. A `gate_approval_test` továbbra is éles hatás nélküli próba. A valódi konfigurációs eszközök diff-, mentési és engedélyezőlista-követelményei nem változnak.

## Bekapcsolás és titokkezelés

A `HomeAssistantApproval` szakasz teljes hiányában nincs csatornaregisztráció, HA-options induláskori validálás, HA-tokenlekérés vagy HTTP/WS-kapcsolat. A Program elindul; csatorna nélkül a magas kockázatú kérés elutasított, nem automatikusan jóváhagyott. A külön `Secrets:SelfTestSecretId` öntesztet ettől függetlenül lehet bekapcsolni; az önteszt nem a csatorna előfeltétele.

Megadott üres, hiányos, ismeretlen kulcsot vagy hibás értéket tartalmazó szakasz esetén induláskori validálás és fix hiba történik: „A Home Assistant jóváhagyási konfiguráció érvénytelen.” A konfiguráció értékét nem visszhangozzuk. A kulcsnevek a .NET konfigurációjával összhangban kis-/nagybetűtől függetlenek.

| Kulcs a szakaszon belül | Jelentés |
| --- | --- |
| `BaseUrl` | Kötelező, legfeljebb 2048 karakteres HA-origin; alapból HTTPS, gyökérútvonallal. |
| `AllowHttp` | Alapból `false`. `true` mellett is csak a meglévő #22 validátor szerinti kanonikus RFC1918 IPv4-literál használhat HTTP-t. |
| `TokenSecretId` | Kötelező, nem üres Bitwarden-titok-GUID szövege. Nem maga a token. |
| `NotifyService` | Kötelező, kizárólag `notify.` és kisbetűvel kezdődő, kisbetű/szám/aláhúzás szolgáltatásnév, legfeljebb 135 karakter. |
| `ExpectedUserId` | Kötelező HA-felhasználóazonosító; 1–128 ASCII betű/szám/aláhúzás/kötőjel. Nem megjelenítési név. |
| `ExpectedDeviceId` | Opcionális, ugyanilyen alakú azonosító; csak jelen lévő eseménymezővel hasonlítjuk össze. |
| `TimeoutSeconds` | Egész 1–30, alapból 5; a teljes kézfogás, illetve egy-egy REST-küldés határideje a titoklekéréssel együtt. |
| `MaxIdleSeconds` | Egész 5–300, alapból 60; a legutóbbi egyező pongtól (kezdetben a feliratkozás ACK-jától) számított életjel-határidő. Önmagában az eseménycsend nem bontja a kapcsolatot. |

A HA-token kizárólag az `ISecretProvider` útján érkezik. Nincs inline tokenkulcs vagy új környezeti token-visszaesés. Szabolcs teszi a HA-titkot a Bitwardenbe; a gate gépi fiókjának credential-kezelése változatlan. Tokent nem kérünk issue-ba, PR-ba vagy szobába.

A `BaseUrl` ellenőrzése az olvasási HA-kliens meglévő validátorát használja. HTTP-n nincs hostnév-, publikus IP-, loopback-, link-local-, IPv6-, alternatív IPv4-írásmód-, URL-paraméter- vagy útvonalkivétel. Az explicit HTTP-engedély biztonságos, cím/token nélküli induláskori figyelmeztetést ad: a Bearer-token a hálózaton titkosítatlan lehet. HTTP és WS nem követ redirectet, nem használ proxyt vagy cookie-t. A WS-cím kizárólag ugyanebből az originből származik: HTTPS → WSS, HTTP → WS, `/api/websocket`; külön WS-host nem állítható be.

## Bizalmi határ és üzemeltetési feltételek

Szabolcs által elfogadott előfeltétel a gate dedikált, admin jogú HA-felhasználójának tokenje. A vizsgált upstream Core `subscribe_events` kezelő a nem engedélyezőlistás eseményekhez adminjogot kér; a `mobile_app_notification_action` nincs a listán. A telepített HA/Companion verzióval ezt üzemeltetéskor újra ellenőrizni kell. A gate nem hoz létre felhasználót és nem ad jogosultságot.

A Companion gombválasza a WS-boríték `event.data.action` mezőjében érkezik. Az ellenőrzött személyazonosító az `event.context.user_id`, soha nem a `data.user_id`. A Core mobil-webhook `registration_context` segédje a regisztrált felhasználót adja a contexthez: ez a regisztrációhoz kötés, nem a fizikai gombnyomó személyének független bizonyítása. A HA-admin, a HA-eseménybusz és a Companion-regisztráció titkainak birtokosa a bizalmi határon belül van.

Az opcionális eszközmező `event.data.device_id`. Csak akkor hasonlítjuk össze, ha a konfigurációban ÉS az eseményben is jelen van. Ekkor eltérés, null, nem szöveges vagy érvénytelen alakú érték tilt. Ha bármelyik oldal hiányzik, az önmagában nem tiltás; a user, kód és lejárat ellenőrzése változatlanul kötelező. A Companion példája nem garantál ilyen mezőt, a vizsgált Core útvonal nem ad hozzá garantált eszközazonosságot. HA device-registry ID-t, app ID-t vagy notify szolgáltatásnevet nem tekintünk egymással felcserélhető azonosítónak; az adott kliens mezőjének jelentését Szabolcs ellenőrzi.

A vizsgált HA mobil-webhook debug szinten teljes payloadot naplózhat. A gate nem tudja kitakarni a HA, reverse proxy vagy mobilapp saját HTTP/WS/debug/notification trace-eit. Ezek nyers megőrzését Szabolcsnak biztonságosan kell kezelnie; Bruce nem módosít éles naplóbeállítást. A gombkód szükségszerűen áthalad a megbízható HA/Companion protokollon, de a gate nem naplózza.

A jelenlegi egyetlen MCP-hívó Mikaela. Az `Audit:Caller` szerveroldali címke és a közös MCP Bearer-token nem többügynökös hitelesítés. Második hívó hozzáférése előtt külön issue-ban agentenkénti tokenes hitelesítés szükséges.

## Kérés, döntés és hibák

Elérhetőség csak a sikeres WS-auth és az `id=1` mobile-action feliratkozás sikeres ACK-ja után van. Új kérés offline állapotban nulla push-sal elutasított. A push a konfigurált `POST /api/services/notify/<szolgáltatás>` végpontnak megy. A szöveg a kapu már engedélyezőlistázott összefoglalóját, hívóját, toolját, magyar kockázati címkéjét, budapesti lejáratát és rövid kérésazonosítóját mutatja, további titokszűréssel és hosszkorláttal. Nem másolja ki a teljes tárolt műveletet.

A címkék: `SensitiveControl` → „érzékeny vezérlés”, `Configuration` → „konfiguráció”, `System` → „rendszer”. A `Lejárat (Europe/Budapest): HH:mm` 24 órás megjelenítés a változatlan UTC-pillanatból, a rendszer valódi `Europe/Budapest` időzónaszabályaival készül, téli/nyári idővel. Nem használ host-local zónát, rögzített eltolást vagy kultúrafüggő időformátumot. Hiányzó/hibás időzóna esetén fix „A jóváhagyási kérés nem küldhető.” hiba van, nincs UTC- vagy helyi visszaesés. A gépen ehhez elérhető időzónaadat szükséges. Új konfigurációs kulcs és NuGet-függőség nincs.

A paraméterek `név: érték` sorok az audit engedélyezőlistájával már képzett, szűrt JSON-összefoglalóból, nem a teljes PreparedOperationből. Csak stringérték jeleníthető meg; null → `[nincs érték]`, összetett/nem támogatott érték → `[kihagyva]`, üres objektum → `[nincs paraméter]`. Kihagyott mezőt nem egészítünk ki. Hibás/nem objektum/túlméretes összefoglaló kizárólag `[kihagyva]`, soha nem nyers JSON-fallback. A hívó legfeljebb 128, az összes paramétersor együtt és az eredményszöveg legfeljebb 512 karakter marad. Az ismert token eltávolítása a dekódolt neveken/értékeken, minden vágás előtt történik; a teljes üzenet is kiszűri. A mezők sortörései és vezérlőkarakterei, valamint a BMP sík bidi/formázó és Unicode sorelválasztó karakterei szóközzé válnak: nem hozhatnak létre új fejlécet vagy döntési sort. A vizsgálat UTF-16 kódegységeken fut, nem teljes Unicode-skalárszűrés; a supplementary sík formázó karaktereire nem ígér szűrést. A megjelenítés nem módosít műveletet, hash-t, gombkódot vagy UTC-lejáratot.

A két gomb egymástól független, kriptográfiai véletlen 32 bájtos kódot kap (64 kisbetűs hex karakter). Nincs kérés-ID- vagy döntésprefix-alapú kód. A kód–döntés–kérés összerendelés rögzített. Csak megfelelő feliratkozás/eseménytípus, user, opcionális eszköz, ismert kód és szigorúan jövőbeli kapuoldali lejárat ad döntést. Egy elfogadott döntés atomikusan mindkét gomb lehetőségét lezárja; verseny és replay nem ad második döntést. A WS-esemény ideje nem hosszabbítja a kérést. A kapu közvetlenül végrehajtás előtt továbbra is újraellenőrzi a hash-t, lejáratot, egyszerhasználhatóságot, auditot és vészleállítást.

A csatorna legfeljebb 1024 értesítést és 256 átadásra váró döntést tart memóriában. Telítettség nem jelent jóváhagyást. A lejárt kódot a döntésellenőrzés tiltja, de az eredményértesítés metaadata a kapu végállapot-jelzéséig megmarad: új kérés nem tüntetheti el az online lejáratpushát. A végállapot értesítése eltávolítja a bejegyzést és a gombokat, offline/hálózati hiba esetén is. A kérések, kódok és engedélyek restart után nem élednek fel az auditból.

Kézfogás, REST-küldés és üzenetvárás megszakítható és határidős; HTTP-válasz és WS-üzenet legfeljebb 65536 bájt, a JSON legfeljebb 16 szint. Nyers keret, header, body, HA-hiba, token vagy kivétellánc nem jut diagnosztikába. Az ismert HA-tokent még a szövegvágás előtt kitakarjuk, hogy a vágás se hagyhasson tokenrészletet. A HTTP szolgáltatáshívás csak sikeres státusz és érvényes, korlátos HA-válasz után számít visszaigazoltnak. Ez HA-oldali elfogadás, nem a telefon kijelzőjére történő kézbesítés bizonyítéka.

A titoklekéréshez a kapu meglévő, felügyelt munkatulajdonlását használjuk: a WS és REST együtt legfeljebb egy aktív providerhívást tarthat. A hívás szinkron belépése sem blokkolja a kézfogási határidőt. A várakozási timeout nem állítja meg a Bitwarden SDK már futó natív hívását; ezt nem állítjuk valódi transportmegszakításnak. A munka tényleges befejezéséig foglalt marad a hely, reconnect nem indíthat új providerhívásokat, a késői eredményt eldobjuk, a kivételt megfigyeljük. A nyilvántartott munkafeladat eredménye nem titokérték. Leállításkor a host megszakítási határidejéig várunk a munkára; a nem megszakítható natív munka a tényleges befejezésig felügyelten megmarad, nem korlátlanul újraindított vagy sikeresnek tekintett kérésként.

Az aktív kapcsolat HA alkalmazásszintű életjelet használ: `{"id": N, "type": "ping"}` → azonos egész `id`-jű `pong`. A ping a feliratkozás azonosítója után kapcsolatonként szigorúan növekvő azonosítót kap. Az alapértelmezett időköz 20 másodperc; kisebb `MaxIdleSeconds` mellett annak egyharmadára csökken, egész ezredmásodpercre lefelé kerekítve. Egyszerre legfeljebb egy ping vár válaszra. Csak annak az egyező pongja indít új életjel-ablakot; más azonosítójú, régi, hibás vagy kéretlen pong, illetve jóváhagyási esemény nem fedheti el az elmaradó pongot. Csendes, de válaszoló HA mellett nincs újracsatlakozás vagy újabb WS-hitelesítési tokenlekérés. A REST push saját tokenlekérése ettől függetlenül megmarad.

Ha az egyező pong az utolsó igazolt életjel óta eltelt `MaxIdleSeconds` határáig nem érkezik meg, a kapcsolat nem elérhetővé válik és újracsatlakozik. A pingküldés is korlátos: legfeljebb `TimeoutSeconds`, illetve a hátralévő életjel-határidő közül a rövidebb ideig vár. A heartbeat nem szakítja meg a folyamatban lévő vagy töredékes fogadást, nem indít párhuzamos receive-et; a közben érkező jóváhagyási események továbbra is feldolgozódnak. Kapcsolatbontás/leállítás megszakítja és megfigyeli a tulajdonolt fogadási feladatot és megszünteti az időzítőket. A heartbeat nem módosítja a jóváhagyási kérések lejáratát.

A reconnect visszavárakozása 1 másodpercről legfeljebb 30 másodpercre nő; minden kapcsolat új authot és feliratkozást végez. A pending pushokat nem küldjük újra, a lejáratot nem hosszabbítjuk. Kapcsolat nélkül a kapu lejáratfigyelése tovább működik; a közben elveszett HA-eseményekhez nincs tartós kézbesítési sor.

Végrehajtás, elutasítás, lejárat és hiba után rövid, szűrt eredménypush megy, gombok nélkül. Az eredményértesítés egyszeri kísérlet: offline állapot, hibás válasz vagy timeout esetén nincs automatikus újraküldés, rollback vagy új végrehajtás. Az eredeti kérés státusza a `gate_approval_status` eszközzel ellenőrizhető. Értesítési siker nem állítható HA-visszaigazolás nélkül.

## Ellenőrzés és források

A tesztek a valódi Program/MCP/audit/kapu útvonalat is fake HTTP-, WebSocket- és `ISecretProvider` függőségekkel járják be, számlált ártalmatlan végrehajtással, véletlen canary titkokkal és strukturált logellenőrzéssel. Nincs valós DNS, HA- vagy Bitwarden-hívás. A végső éles próbát kizárólag Szabolcs végezheti a `gate_approval_test` eszközzel.

A tervezéskor újraolvasott hivatalos források upstream állapotot írnak le, nem a helyi telepítés igazolását:

- [Companion actionable notifications](https://companion.home-assistant.io/docs/notifications/actionable-notifications/)
- [HA WebSocket API](https://developers.home-assistant.io/docs/api/websocket/)
- [HA REST API](https://developers.home-assistant.io/docs/api/rest/)
- [Core registration_context](https://github.com/home-assistant/core/blob/dev/homeassistant/components/mobile_app/helpers.py)
- [Core mobile webhook és debug payload](https://github.com/home-assistant/core/blob/dev/homeassistant/components/mobile_app/webhook.py)
- [Core subscribe_events jogosultság](https://github.com/home-assistant/core/blob/dev/homeassistant/components/websocket_api/commands.py)
- [Core esemény-engedélyezőlista](https://github.com/home-assistant/core/blob/dev/homeassistant/auth/permissions/events.py)
