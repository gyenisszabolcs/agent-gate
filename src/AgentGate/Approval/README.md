# Aszinkron jóváhagyási kapu és egyszer használható tároló

A `Program` a közös MCP-auditkapu mellett singleton `ApprovalGate` háttérszolgáltatást és az alábbi egyszer használható tárolót regisztrálja. A-ban nincs éles csatorna vagy automatikus jóváhagyó; csatornaregisztráció nélkül minden jóváhagyásköteles kérés elutasított. A #30 [opcionális Home Assistant csatornája](HomeAssistant/README.md) erre a kapura épül.

## Alkalmazásszintű folyamat

- `Read` és `Control` továbbra is a meglévő audit- és vészleállító-kapun át, közvetlenül fut. `Forbidden` soha. `SensitiveControl`, `Configuration` és `System` az eredeti MCP-delegált helyett a közös jóváhagyási kapuba kerül.
- Csak kódban regisztrált `IApprovalOperationHandler` készíthet validált, normalizált `PreparedOperation` pillanatképet. Ismeretlen handler vagy eltérő tool/kockázat elutasított. Nincs tetszőleges nevű/szolgáltatású végrehajtó MCP-eszköz. Új handler külön biztonsági és tesztfeladat: minden végrehajtási paramétert, célrendszert, célt és defaultot ki kell bontania; a későbbi végrehajtás nem olvashat új értéket a korábbi kérésből.
- A pillanatkép saját `FrozenDictionary<string,string?>` másolat, nem request-context vagy a kérés `next` delegáltja. A végrehajtó egy új szolgáltatásscope-ból kapja az immutable műveletet és a gate leállítási tokenjét.
- Az előzetes tartós audit után a gate rövid, engedélyezőlistás és titokszűrt összefoglalót küld. Sikeres küldés után az MCP azonnal `jóváhagyásra vár: <id>` választ ad, emberi döntésre nem vár. A kezdeti audit `Pending`, nem végrehajtási siker. Az audit CallId egyben a jóváhagyási kérés azonosítója.
- A függő kérés önmagában nem hoz létre `Create`-engedélyt. A küldéssel párhuzamosan visszaérkező döntés is megvárja a kezdeti Pending-audit véglegesítését. Annak hibája nem nyithat végrehajtási jogot. A véglegesítés előtt észlelt kliensmegszakítás érvényteleníti a kérést. A független, korlátos auditvéglegesítés közben vagy utána érkező megszakítás viszont nem vonja vissza a sikeresen tartósított Pending kérést; a későbbi végrehajtást kizárólag a gate leállítási tokenje vezérli.
- Az `IApprovalChannel` csak megbízható szerveroldali integráció lehet: elérhetőség, kérésküldés, ellenőrzött döntések aszinkron folyama, eredményküldés. A döntés beadhatósága nem MCP-funkció. A csatorna az ellenőrzött döntéshozót, döntést és időt adja át; A nem állít fizikai személyazonossági garanciát egy fake vagy későbbi integráció helyett.
- Atomikus kéréslefoglalás után a gate auditálja a döntést. Közvetlenül a művelet előtt tartós execution-audit, friss vészleállító-olvasás, lejárat-, hash- és egyszerhasználhatóság-ellenőrzés történik. Csak ekkor keletkezik és azonnal elfogy az engedély. A `now >= expiresAt` határon már nincs végrehajtás, az audit alatt elért lejárat is lejárat.
- Döntésismétlés és párhuzamos döntés nem indít új végrehajtást. Megkezdett vagy hibás műveletet a gate nem próbál automatikusan újra. Nincs folyamatösszeomláson átívelő elosztott exactly-once tranzakció vagy visszagörgetési ígéret.
- A végrehajtási állapot és eredmény, a döntés és a lejárat a kérésazonosítóval korrelálható tartós auditot kap. Előzetes audithiba mellett nincs művelet. Utólagos audithiba sikertelen állapotot jelezhet, miközben a művelet már lefutott; a Started sor nem rollback-bizonyíték. Nyers kivétel nem kerül a válaszba, auditba vagy loggerbe.
- A `Configuration` szint nem váltja ki a diffet, automatikus mentést és fájlengedélyezőlistát. Nincs valódi konfigurációmódosító handler ebben a modulban. Az ilyen követelményeket külön megvalósításnak és teszteknek kell biztosítaniuk, mielőtt valós konfiguráló tool regisztrálható.

## Eszközök és egyhívós bizalmi határ

- `gate_approval_test`: `SensitiveControl`, kötelező `text` string. NFC-normalizált, szélein vágott 1–160 karakter, belső vezérlőkarakter nélkül; ismeretlen paraméter elutasított. Nincs éles hatása: csak a végrehajtás tényét adja vissza. A szűrt összefoglaló kizárólag a `text` auditparaméterből készül.
- `gate_approval_status`: `Read`, `requestId` szabványos D-formátumú GUID. Várakozik, jóváhagyva és végrehajtva, elutasítva, lejárt vagy sikertelen állapot és legfeljebb a szűrt eredmény. Az azonosító ismerete nem jogosultság.
- A telepítés jóváhagyott feltétele: kizárólag Mikaela fér hozzá a gate-hez. Az `Audit:Caller` szerveroldali hívónevet üzemeltetéskor Mikaelára kell állítani; a kérés tulajdonosa ennek létrehozáskori pontos értéke. A status csak az aktuális konfigurált név egyezésekor ad adatot. Kliens `caller`/`agent` paraméter nem írhatja felül; idegen és ismeretlen kérés azonos választ kap.
- A közös Bearer-token és a konfigurált hívónév nem képez független agentprincipalokat. Második hívó hozzáférése ELŐTT külön issue-ban agentenkénti tokenes hitelesítés szükséges. Ez a modul nem tesz többhívós biztonsági ígéretet és nem vezet be új hitelesítést.

## Konfiguráció és életciklus

`Approval:LifetimeMinutes`: alapból 15, egész 1–1440. `Approval:SendTimeoutSeconds`: alapból 5, egész 1–30. Hibás érték fix, bemenetet nem visszhangzó indulási konfigurációs hibát ad. Az időforrás DI-ból cserélhető `TimeProvider`.

Csatorna hiányában nincs HA-/Bitwarden-hívás. A küldési várakozás korlátos és megszakítható; egyszerre legfeljebb egy nyilvántartott kérés- vagy eredményküldés folyik. A ténylegesen be nem fejeződött munka timeout után is foglalja a helyét, ezért új munkák nem halmozhatók rá. Leállításkor a gate megszakítja és a host határidejéig megvárja. .NET cancellation nem tud kényszerrel leállítani együtt nem működő külső implementációt: legfeljebb ez az egy nyilvántartott feladat maradhat késői befejezésre; késői hibája sem kerül naplóba. Az eredményküldés hibája nem módosítja a már meghozott döntést és nem indít műveletismétlést. Foglalt/offline csatornánál nem állítunk sikeres kézbesítést.

A végállapotoknak külön, legfeljebb 1024 elemű átadási sora és egyetlen felügyelt küldőhurka van. A foglalt küldési hely nem dobja el az eredményt: a végállapot első küldési kísérlete megvárja a hely felszabadulását, és megelőzi az új kérdésküldést. Ez nem sikertelen kézbesítés újrapróbálása; az elkezdett eredményküldést nem ismételjük. A sikeres push utáni Pending-audithiba is lezáró Failed-jelzést ad a csatornának, végrehajtási jog nélkül. Ha a szinkron lezárási jelzés nem fér a sorba, új kéréseket zártan tiltunk; a HA-csatorna 1024-es értesítési korlátja mellett a sor nem nőhet korlátlanul.

A külső küldési timeout vagy kliensmegszakítás után eltávolított kéréshez is egyszeri lezárás tartozik, ha a csatornahívás ténylegesen elindult. A késői küldési siker nem hagyhat árva értesítést: a felügyelt munka befejezése után az eredményhurok visszavonja, végrehajtási engedély nélkül. A még el sem indult, foglalt hely miatt elutasított küldés nem termel üres lezárásokat.

A döntésolvasó hibáját a gate nem naplózza nyersen; új kéréseket zártan elutasít, a már függő kérések lejárata tovább működik. Lejáratkor a 100 ms-os háttérfigyelő auditál és eredményjelzést sorba állít. Leállításkor a még el nem kezdett értesítés elveszhet; nem állítunk kézbesítést. A státuszok memóriabeli története jelenleg a gate folyamatának élettartamáig megmarad; tömeges használat előtt külön kapacitás-/megőrzéspolitika szükséges.

A függő kérések, eredmények és engedélyek csak memóriában élnek. Gate-újraindítás után MIND érvénytelenek, az audit megmaradása nem éleszti fel őket. Egyetlen gate-példányra vonatkozó garancia, nem többpéldányos elosztott végrehajtás.

## Kanonikus műveletséma V1

A gate UTF-8 (BOM nélkül) JSON-reprezentációja fix mezősorrendben: `version` (1), `owner` (pontos szerveroldali hívónév), `tool` (regisztrált név), `risk` (enum egész érték), `parameters` (minden hatásos normalizált paraméter és kibontott default). A paraméterkulcsok ordinal sorrendűek; az értékek normalizált stringek vagy JSON nullok, a .NET alapértelmezett `System.Text.Json` escapingjével. Összetett/typed paraméterek egyértelmű normalizált reprezentációja az adott handler szerződése. A SHA-256 lenyomat kisbetűs hex. Nem az összefoglalót vagy az eredeti nyers MCP-JSON-t hash-eljük. Ekvivalens normalizált bemenet azonos hash-t, más rejtett/hatásos paraméter vagy tulajdonos más hash-t ad. A teljes művelet és hash nem megy a csatornára vagy az auditba.

## API és bizalmi határ

- `new InMemoryApprovalStore(timeProvider)`: kötelező, nem null időforrás; éles bekötéskor használható a beépített `TimeProvider.System`.
- `Create(operationHash, expiresAt)`: egyedi `Guid` azonosítójú, módosíthatatlan `ApprovalRequest` létrehozása. A lejárat csak nulla eltolású, jövőbeli UTC `DateTimeOffset` lehet. Nincs automatikus időzóna-átváltás.
- **A `Create` már jóváhagyott engedélyt tárol.** Nem függő kérés vagy emberi döntés helyettesítője. Csak a megbízható jóváhagyó réteg hívhatja az ember döntése után; az agent számára nem szabad közzétenni. A modell önmagában, illetve az azonosító ismerete nem bizonyít jóváhagyást: a tároló dönt.
- `TryConsume(id, operationHash)`: csak létező, még érvényes és pontosan egyező hash-ű bejegyzésnél `true`; siker esetén eltávolítja a bejegyzést. A nem létező (az üres azonosítót is beleértve), elfogyasztott, lejárt vagy más hash-hez kötött engedély eredménye `false`.
- `now >= expiresAt` már lejárt. Az időolvasás, ellenőrzés és eltávolítás ugyanabban a példányszintű zárolásban történik, így párhuzamos fogyasztásból legfeljebb egy sikerül. Az észlelt lejárat szintén eltávolítja a bejegyzést, ezért az óra későbbi visszaállása nem támasztja fel.
- Hibás bemenetre magyar szövegű `ArgumentException`, null időforrásra `ArgumentNullException` keletkezik; a hibaszöveg nem ismétli meg a bemenetet. Eltérő vagy hibás formátumú hash nem fogyaszt el még érvényes engedélyt.

## Művelethash-szerződés

A hash **SHA-256**, pontosan **64 kisbetűs ASCII hexadecimális karakter** (`[0-9a-f]{64}`), prefix nélkül. Mindkét publikus művelet ellenőrzi ezt. Null, üres, nem megfelelő hosszúságú, nagybetűs, szóközös, sortöréses vagy nem ASCII bemenet elutasított; nincs trim, kisbetűsítés vagy más csendes normalizálás. Összehasonlítása ordinális és pontos.

A tároló kizárólag a lenyomatot kapja, az eredeti műveletet nem tárolja és nem tudja ellenőrizni. **A bekötő réteg felelőssége** a jóváhagyáskor megmutatott és a végrehajtandó művelet azonos, változatlan bájtreprezentációjának SHA-256 hash-elése. Ennek verziózott, egyértelmű szerializációban tartalmaznia kell a hívó identitását/jogosultsági kontextusát, a művelet nevét, a feloldott célrendszert/célazonosítót és minden hatásos paramétert (az alapértékekkel együtt). Elválasztó nélküli mezőösszefűzés, önkényes JSON-/Unicode-normalizálás vagy jelentős mező elhagyása nem megengedett. Szöveges szerializációnál ugyanazt az UTF-8, BOM nélküli bájtsorozatot kell hash-elni mindkét oldalon. Az integráció saját, konkrét szerializációs verzióját és mezősémáját külön rögzítse; az `InMemoryApprovalStore` önmagában nem választ és nem alakít át műveleti sémát. A bekötött kapu V1 sémája fent található.

A forma ellenőrizhető, de önmagában nem bizonyítja a lenyomat helyes előállítását vagy az ember jóváhagyását. A visszaadott modellt a hívó nem módosíthatja. A tesztekben a hash a .NET `SHA256.HashData` és `Convert.ToHexStringLower` futtatásával készül, kizárólag szintetikus, titokmentes műveletszövegből.

## Élettartam és korlátok

Minden állapot a tároló egyetlen példányában él. Nincs statikus/processzszintű állapot, fájl vagy adatbázis. **Újraindításkor és példánycserénél minden jóváhagyás elveszik**, az új tároló az előző azonosítókat elutasítja. Több példány nem oszt meg engedélyeket; a majdani bekötésnek egy közös példányt kell használnia a kérés és a fogyasztás útvonalán.

A sikeresen elfogyasztott és a fogyasztáskor lejártnak észlelt elemek törlődnek. Nincs háttértakarító vagy kapacitáskorlát: a soha többé meg nem keresett bejegyzések a példány élettartamáig memóriában maradhatnak. Tömeges/éles használat előtt külön tervezendő az erőforráskorlát és a takarítás. UTC falióra az időalap; egy még nem észlelt lejáratot az óra visszaállítása késleltethet. Az engedély elfogyasztása nem tranzakció a későbbi végrehajtással: annak hibája esetén sem használható újra.

Új NuGet-függőség nincs: .NET 10 `TimeProvider`, `Dictionary`, példányszintű zárolás és meglévő xUnit használatos. A rövid, kizárólag memóriabeli API szinkron; nincs I/O, háttérfeladat vagy mesterséges aszinkron burkolás.
