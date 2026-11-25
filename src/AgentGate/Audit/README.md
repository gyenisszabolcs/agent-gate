# MCP-audit és SQLite-tároló

## Közös eszközhívási kapu

A `McpAuditFilter` az SDK közös `tools/call` filtere. Minden regisztrált eszköznek explicit `ToolRisk` és `ToolAuditParameters` metaadat szükséges; az üres naplóparaméter-lista is explicit. Az attribútum csak valóban auditálható paraméterneveket engedélyezzen.

Az engedélyezett skalárértékeken a `SecretRedactor` fut. Objektum, tömb és nem engedélyezett érték helyett `[kihagyva]` szerepel. A nem megbízható paraméternevek sem másolódnak át: közös jelölő helyettesíti őket. Jelöletlen nyers kivétel és MCP-hibapayload helyett rögzített, szűrt hibaszöveg kerül az auditba; a sikeres eszközválasz nem auditadat.

A tartós `BeginAsync` után, a végrehajtás előtt a [fájlos vészleállító](../Policy/KillSwitch.md) friss állapotát is ellenőrizzük. Tiltáskor nincs eszközvégrehajtás, a lezárás `Failed`, a biztonságos indok a válaszban és az auditban azonos.

## Kifejezetten jelölt eszközhiba

Az eszköz fejlesztője `AgentGate.Tools.GateToolException` kivétellel jelezhet kiadhatónak szánt hibát. Csak a közvetlenül így jelölt típus `Message` mezője jut át, kötelező `SecretRedactor.RedactText` szűrés után, egyébként változatlanul. A válasz `IsError=true`, a lezárás `Failed`; az audit hibamezője ugyanaz a szűrt üzenet. Stack, `Data` és belső kivétel nem része a kiadási szerződésnek.

Más kivétel (a jelöletlen SDK `McpException` is), becsomagolt jelölő és tetszőleges `IsError` eredmény szövege/strukturált tartalma továbbra is az általános `Az eszköz végrehajtása sikertelen.` hibára cserélődik. A szövegegyezés nem típusbizalom, a hibapayload önmagában soha nem jelöl biztonságos üzenetet. A rögzített SDK 2.4.0 regisztrált eszközének közvetlen kivételét a közös filter még típushelyesen megkapja; ez nem az SDK tetszőleges hibakonverziójának visszafejtése. Új SDK vagy egyedi belső adapter ezt a határt újraellenőrzendővé teszi.

A jelölt üzenet sem automatikusan titokmentes: a heurisztikus szűrő tetszőleges jelöletlen credentialt nem ismer fel. A fejlesztő ne jelöljön providerből másolt nyers üzenetet biztonságosnak. Az audit elő-/utóírási hibája továbbra is felülírja az eszköz eredményét a korábbi zárt hibával; a lezárási hiba nem visszagörgetés.

## Modulok bekötése

A modul a saját szolgáltatásregisztrációjában köti be az `IToolCallPolicy`, `IOperationErrorPolicy` és jóváhagyásköteles műveletnél az `IApprovalOperationHandler` megvalósításait. Új modul miatt a közös audit- és jóváhagyási kaput nem kell módosítani. A `ToolRisk` és `ToolAuditParameters` metaadat továbbra is kötelező; a modulpolicy sem a kockázati besorolást, sem az auditot, vészleállítót vagy jóváhagyást nem helyettesíti.

Az `IToolCallPolicy.Handles` csak a modul saját eszközeit jelölje ki. A `ValidateArguments` az SDK-binder előtt ellenőrizze a nyers argumentumok engedélyezőlistáját. Az `EnrichAuditParameters` kizárólag validált, naplózható mezőket adjon hozzá; az eredményre a közös paraméterszűrés változatlanul lefut.

Az `IOperationErrorPolicy` explicit kivételtípus és szükség esetén a jóváhagyott `PreparedOperation` alapján adhat vissza saját, rögzített kategóriaüzenetet. Közvetlen hívásnál a művelet null; ismeretlen típusnál vagy nem illő műveletnél az eredmény is null. Providerüzenet, belső kivétel és SDK-hibapayload nem válik ettől megbízhatóvá. A kapu a kiadható üzenetet egyszer titokszűri, és ugyanazt használja a válaszhoz és az audithoz. Audit-lezárási hibát a jóváhagyási kapu nem ad át modulhibaként.

A HA-modul `HomeAssistantControlCallPolicy` osztályában marad a két vezérlőeszköz zárt argumentumsémája, a normalizált adatok és a TTS-szöveghossz auditálása, valamint a korábban is kiadható kvóta-, csendesóra- és TTS-végrehajtási hibák szűk besorolása. Jóváhagyott TTS-hiba továbbra is csak a `ha_sensitive_control` / `media_player` / `speak` művelethez tartozhat.

## Auditkonfiguráció

```json
{
  "Audit": {
    "DatabasePath": "/var/lib/agentgate/audit.db",
    "Caller": "agent"
  }
}
```

Ezek az alapértékek; a szokásos .NET konfiguráció, például `Audit__DatabasePath` és `Audit__Caller` felülírhatja őket. A hívó szerveroldali konfigurációból származik, nem a kliens önbevallásából. A szülőkönyvtárnak léteznie és írhatónak kell lennie; a modul nem hoz létre telepítési könyvtárat.

## Tartós hívásállapot

Az `IAuditStore.BeginAsync` GUID-azonosítóval, UTC időponttal, hívóval, eszközzel, kockázattal és titokmentes paraméterekkel `Started` sort tartósít. Ennek sikere előtt az eszközkód nem futhat. Írási hiba biztonságos eszközhibát ad, végrehajtás nélkül.

A `CompleteAsync` csak a megfelelő azonosítójú `Started` sort zárhatja `Succeeded`, `Failed`, `Cancelled` vagy jóváhagyásra váró `Pending` állapotba, nemnegatív, véges időtartammal és opcionális titokmentes hibával. Az időtartam a kezdőbejegyzés utáni szakaszt méri, a lezáró adatbázisírást nem; Pending esetén nem a későbbi emberi várakozás vagy végrehajtás ideje. Ismételt vagy nem létező lezárás elutasított.

Lezárási hiba nem ad teljes sikert, és nem görgeti vissza a már lefutott eszközt: a sor `Started` állapotban marad. A kliens megszakítását az eszköz megkapja; a lezárás külön, öt másodperces határidejű tokent használ. Ez nem folyamatkilövési vagy azonnali natív I/O-megszakítási garancia.

Ismeretlen vagy hiányos metaadatú hívásnál nincs eszközvégrehajtás, de írható tárolónál `Failed` elutasítási audit készül. Az ismeretlen név fix marker, az argumentumok kimaradnak. Hiányzó/érvénytelen kockázatnál a `Forbidden` elutasítási besorolás, nem egy ismeretlen eszköz feltételezett üzleti kockázata. Érvényes kockázat hiányzó engedélyezőlistánál megmarad.

## Tároló API és kompatibilitás

A külön `CompletePendingAsync(callId, state, sanitizedError, cancellationToken)` kizárólag a pontosan egyező `CallId`-jú, még `Pending` eredeti sort zárja `Rejected`, `Expired`, `Failed` vagy sikeres végrehajtásnál `Succeeded` állapotba. Az SQL feltételes frissítése párhuzamos lezárásból is csak egyet fogad el; hiányzó, `Started` vagy már lezárt sor érintetlen marad. A hívó, tool, eredeti paraméterek, időpont és korábbi időtartam nem változik. A `CompleteAsync` egyszeri Started-védelme változatlan.

A teljes jóváhagyási `requestId` az eredeti `AuditCall.CallId`. Az `approval_lifecycle` sorok `event` mezője `request_sent`, `approved`, `rejected`, `expired`, `executed` vagy `failed`; a korábbi `phase` mező (`decision`, `outcome` is) megmarad. Az esemény a megtörtént lépést nevezi meg, a `state` a kapu az íráskori pillanatképe: a döntés auditálása még a végrehajtás előtt történik. Az `approved` nem végrehajtási siker, a `request_sent` csak visszaigazolt csatornaküldés, nem telefonos kézbesítési bizonyíték. A külön execution-audit is teljes `requestId`-t tartalmaz.

Auditírási hiba esetén nincs garantált tartós végállapot: a korábbi `Started` vagy `Pending` sor megmaradhat. A kapu rögzített hibát jelez; a külön `failed` esemény írása is meghiúsulhat. Ez nem a hibás írás vagy a művelet újrapróbálása, és nincs rollback. Új sémamigráció, történeti Pending-sorok átírása és auditból helyreállított végrehajtási engedély nincs. Az `AuditEntries` és az `AuditCalls` meglévő sémája változatlan; új konfigurációs kulcs vagy NuGet-függőség nincs.

- `new SqliteAuditStore(databasePath)`: teljes fájlútvonal meglévő szülőkönyvtárban; nincs relatív, URI- vagy memóriatároló. A konstruktor nem hoz létre fájlt/könyvtárat.
- Az MCP-hívásállapot az `AuditCalls` táblában van. A régi `AppendAsync`/`ReadAllAsync` API és `SanitizedAuditEntry` továbbra is az `AuditEntries` táblát használja, nem az új hívássorokat.
- Az első művelet létrehozza a hiányzó sémát, a régi adatokat nem törli. Minden művelet saját, nem poolozott kapcsolatot használ, paraméterezett SQL-lel és lezárt erőforrásokkal. Nincs háttérpuffer.
- A közvetlen tároló API minden szöveges mezője előzetesen titokmentesített adatot követel; nem önálló titokszűrő. A nyers tárolói kivételek nem továbbíthatók külső naplóba/válaszba; a közös kapu biztonságos hibává alakítja őket.
- A régi `ReadAllAsync` nem lapozott. Nagy mennyiségű adathoz korlátozott lekérdezés szükséges.

## Naplózási és üzemeltetési korlátok

A rögzített SDK a filteren kívül nyers eszköznevet, Trace szinten üzenettartalmat naplózhat. Ezért a `ModelContextProtocol.Server.` kategóriacsalád normál naplózása minden szinten tiltott; providerspecifikus szabály sem kapcsolhatja vissza. Az alkalmazás- és ASP.NET-naplózás megmarad.

Az SDK Warning/Error szintű session- és transportdiagnosztikái is kiesnek. A tartós audit csak eszközhívás-szinten helyettesít: auth előtt elutasított HTTP, filter előtt hibás protokollkérés, initialize/list és más nem-tool művelet nem `AuditCalls` bejegyzés. Külső body logging, telemetry exporter, új SDK vagy saját eszköznapló külön tartalombiztonsági ellenőrzést igényel; a nyers SDK-naplót éles hibakeresésre sem szabad visszakapcsolni.

A szövegszűrés heurisztikus, tetszőleges jelöletlen titkot nem ismer fel. Az eszköz felel a sikeres válasz titokmentességéért. A SQLite provider async API-ja alatt szinkron I/O futhat, ezért azonnali megszakítás nem garantált.

Az adatbázis nem titkosított. Fájl-/könyvtárjogosultság, tárolási gyökér, megőrzés, mentés és kapacitáskezelés a befogadó rendszer feladata. Az útvonal nem jogosultsági sandbox vagy szimbolikuslink-védelem.
