# Egyszer használható jóváhagyási tároló (#11)

Önálló, még be nem kötött modul. Nincs UI, értesítés, MCP-eszköz, műveletvégrehajtás vagy külső kapcsolat.

## API és bizalmi határ

- `new InMemoryApprovalStore(timeProvider)`: kötelező, nem null időforrás; éles bekötéskor használható a beépített `TimeProvider.System`.
- `Create(operationHash, expiresAt)`: egyedi `Guid` azonosítójú, módosíthatatlan `ApprovalRequest` létrehozása. A lejárat csak nulla eltolású, jövőbeli UTC `DateTimeOffset` lehet. Nincs automatikus időzóna-átváltás.
- **A `Create` már jóváhagyott engedélyt tárol.** Nem függő kérés vagy emberi döntés helyettesítője. Csak a majdani megbízható jóváhagyó réteg hívhatja az ember döntése után; az agent számára nem szabad közzétenni. A modell önmagában, illetve az azonosító ismerete nem bizonyít jóváhagyást: a tároló dönt.
- `TryConsume(id, operationHash)`: csak létező, még érvényes és pontosan egyező hash-ű bejegyzésnél `true`; siker esetén eltávolítja a bejegyzést. A nem létező (az üres azonosítót is beleértve), elfogyasztott, lejárt vagy más hash-hez kötött engedély eredménye `false`.
- `now >= expiresAt` már lejárt. Az időolvasás, ellenőrzés és eltávolítás ugyanabban a példányszintű zárolásban történik, így párhuzamos fogyasztásból legfeljebb egy sikerül. Az észlelt lejárat szintén eltávolítja a bejegyzést, ezért az óra későbbi visszaállása nem támasztja fel.
- Hibás bemenetre magyar szövegű `ArgumentException`, null időforrásra `ArgumentNullException` keletkezik; a hibaszöveg nem ismétli meg a bemenetet. Eltérő vagy hibás formátumú hash nem fogyaszt el még érvényes engedélyt.

## Művelethash-szerződés

A hash **SHA-256**, pontosan **64 kisbetűs ASCII hexadecimális karakter** (`[0-9a-f]{64}`), prefix nélkül. Mindkét publikus művelet ellenőrzi ezt. Null, üres, nem megfelelő hosszúságú, nagybetűs, szóközös, sortöréses vagy nem ASCII bemenet elutasított; nincs trim, kisbetűsítés vagy más csendes normalizálás. Összehasonlítása ordinális és pontos.

A tároló kizárólag a lenyomatot kapja, az eredeti műveletet nem tárolja és nem tudja ellenőrizni. **A bekötő réteg felelőssége** a jóváhagyáskor megmutatott és a végrehajtandó művelet azonos, változatlan bájtreprezentációjának SHA-256 hash-elése. Ennek verziózott, egyértelmű szerializációban tartalmaznia kell a hívó identitását/jogosultsági kontextusát, a művelet nevét, a feloldott célrendszert/célazonosítót és minden hatásos paramétert (az alapértékekkel együtt). Elválasztó nélküli mezőösszefűzés, önkényes JSON-/Unicode-normalizálás vagy jelentős mező elhagyása nem megengedett. Szöveges szerializációnál ugyanazt az UTF-8, BOM nélküli bájtsorozatot kell hash-elni mindkét oldalon. A későbbi integráció saját, konkrét szerializációs verzióját és mezősémáját külön rögzítse; ez a modul nem választ és nem alakít át műveleti sémát.

A forma ellenőrizhető, de önmagában nem bizonyítja a lenyomat helyes előállítását vagy az ember jóváhagyását. A visszaadott modellt a hívó nem módosíthatja. A tesztekben a hash a .NET `SHA256.HashData` és `Convert.ToHexStringLower` futtatásával készül, kizárólag szintetikus, titokmentes műveletszövegből.

## Élettartam és korlátok

Minden állapot a tároló egyetlen példányában él. Nincs statikus/processzszintű állapot, fájl vagy adatbázis. **Újraindításkor és példánycserénél minden jóváhagyás elveszik**, az új tároló az előző azonosítókat elutasítja. Több példány nem oszt meg engedélyeket; a majdani bekötésnek egy közös példányt kell használnia a kérés és a fogyasztás útvonalán.

A sikeresen elfogyasztott és a fogyasztáskor lejártnak észlelt elemek törlődnek. Nincs háttértakarító vagy kapacitáskorlát: a soha többé meg nem keresett bejegyzések a példány élettartamáig memóriában maradhatnak. Tömeges/éles használat előtt külön tervezendő az erőforráskorlát és a takarítás. UTC falióra az időalap; egy még nem észlelt lejáratot az óra visszaállítása késleltethet. Az engedély elfogyasztása nem tranzakció a későbbi végrehajtással: annak hibája esetén sem használható újra.

Új NuGet-függőség nincs: .NET 10 `TimeProvider`, `Dictionary`, példányszintű zárolás és meglévő xUnit használatos. A rövid, kizárólag memóriabeli API szinkron; nincs I/O, háttérfeladat vagy mesterséges aszinkron burkolás.
