# Önálló SQLite auditnapló

A modul nincs bekötve az alkalmazásba, és nem függ titokszűrőtől. A `SanitizedAuditEntry` minden szöveges mezője — a hívó, az eszköz és az eredmény is — **kizárólag előzetesen titokmentesített adatot** tartalmazhat. A név szerződés, nem automatikus titokfelismerés. Nyers paraméter, kivételszöveg, token vagy jelszó nem adható át.

## Használat és validálás

- `new SqliteAuditStore(databasePath)`: kötelező, teljes fájlútvonal meglévő szülőkönyvtárral. Nincs alapértelmezett adatbázis, relatív út, SQLite URI vagy memóriatároló. A konstruktor nem hoz létre fájlt/könyvtárat.
- `AppendAsync(entry, cancellationToken)`: UTC (`Offset == 0`) időpont, nem üres hívó/eszköz, nem null titokmentesített paraméter/eredmény, létező `Policy.RiskLevel`. Az üres paraméter/eredmény megengedett. A hibás bejegyzés adatbázis-megnyitás előtt elutasításra kerül.
- `ReadAllAsync(cancellationToken)`: beszúrási sorrendű pillanatnyi lista, minden eredeti mezővel. Új adatbázisnál üres lista. Nem lapozott API; nagy mennyiségű naplóhoz később korlátozott lekérdezés szükséges.
- Az első művelet hozza létre a fájlt és a sémát, ha hiányoznak. Minden művelet saját, nem poolozott kapcsolatot használ, és lezárja az olvasót/parancsot/kapcsolatot. A beszúrás egyetlen atomi SQL-utasítás, minden mező paraméterezett. Nincs háttérpuffer.
- Hozzáférési, sérült adatbázis- és SQLite-hibák továbbterjednek: nincs csendes siker vagy másik adatbázisra visszaesés. A kivételek nem továbbíthatók automatikusan külső naplóba/válaszba.

## Új függőség

Egy közvetlen csomag: **Microsoft.Data.Sqlite 10.0.15**. A Microsoft könnyű ADO.NET SQLite providere; teljes EF/ORM nem szükséges. A normál csomag a natív SQLite futtatókörnyezetet is függőségként biztosítja, így nem kell kézzel telepített gépi SQLite-ra támaszkodni.

A verziót a NuGet elsődleges csomagoldalán és a hivatalos flat-container verziólistájában ellenőriztem; a 10.0-s stabil vonal ellenőrzéskor elérhető legmagasabb javítóverziója 10.0.15 volt. Ez szándékosan a projekt .NET 10-es vonalához igazított, rögzített verzió, nem az összes főverzió közül választott legújabb.

Elsődleges források:
- https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.15
- https://api.nuget.org/v3-flatcontainer/microsoft.data.sqlite/index.json
- https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async

**Async korlát:** az API aszinkron és a megszakítási tokent minden azt fogadó ADO.NET hívás megkapja. A Microsoft dokumentációja szerint a SQLite nem biztosít valódi aszinkron I/O-t, ezért a provider async metódusai szinkron módon hajtódnak végre. Folyamatban lévő natív I/O azonnali megszakítását a modul nem ígéri; a már megszakított műveletet elutasítja.

## Kockázat és későbbi bekötés

Közepes kockázat: tartós, nem titkosított auditadat; a hívó felelőssége az előzetes titokmentesítés. Az útvonal explicit konfiguráció, nem jogosultsági sandbox vagy szimbolikuslink-védelem. Éles bekötés előtt külön feladat a fájl/könyvtár jogosultsága, engedélyezett tárolási gyökér, megőrzés, mentés, kapacitáskezelés és a titokszűrés bekötése. E modul nem módosít jogosultságot, és nem ér el éles rendszert.

A saját xUnit tesztek egyedi `Directory.CreateTempSubdirectory` könyvtárakban dolgoznak (a környezeti TMPDIR a scratch könyvtárra mutat), és `Dispose` alatt rekurzívan takarítanak. Csak szintetikus adatokat használnak. Lefedés: teljes mező-roundtrip, tartósság új példánnyal/kapcsolattal, beszúrási sorrend, SQL-karakterek minden szövegmezőben és az útvonalban, adatbázis-izoláció, üres olvasás, hibás/hiányzó útvonal, hibás bejegyzés és előzetes megszakítás.
