# MCP-audit és SQLite-tároló

## Közös eszközhívási kapu

A `McpAuditFilter` az SDK közös `tools/call` filtere. Minden regisztrált eszköznek explicit `ToolRisk` és `ToolAuditParameters` metaadat szükséges; az üres naplóparaméter-lista is explicit. Az attribútum csak valóban auditálható paraméterneveket engedélyezzen.

Az engedélyezett skalárértékeken a `SecretRedactor` fut. Objektum, tömb és nem engedélyezett érték helyett `[kihagyva]` szerepel. A nem megbízható paraméternevek sem másolódnak át: közös jelölő helyettesíti őket. Jelöletlen nyers kivétel és MCP-hibapayload helyett rögzített, szűrt hibaszöveg kerül az auditba; a sikeres eszközválasz nem auditadat.

A tartós `BeginAsync` után, a végrehajtás előtt a [fájlos vészleállító](../Policy/KillSwitch.md) friss állapotát is ellenőrizzük. Tiltáskor nincs eszközvégrehajtás, a lezárás `Failed`, a biztonságos indok a válaszban és az auditban azonos.

## Kifejezetten jelölt eszközhiba

Az eszköz fejlesztője `AgentGate.Tools.GateToolException` kivétellel jelezhet kiadhatónak szánt hibát. Csak a közvetlenül így jelölt típus `Message` mezője jut át, kötelező `SecretRedactor.RedactText` szűrés után, egyébként változatlanul. A válasz `IsError=true`, a lezárás `Failed`; az audit hibamezője ugyanaz a szűrt üzenet. Stack, `Data` és belső kivétel nem része a kiadási szerződésnek.

Más kivétel (a jelöletlen SDK `McpException` is), becsomagolt jelölő és tetszőleges `IsError` eredmény szövege/strukturált tartalma továbbra is az általános `Az eszköz végrehajtása sikertelen.` hibára cserélődik. A szövegegyezés nem típusbizalom, a hibapayload önmagában soha nem jelöl biztonságos üzenetet. A rögzített SDK 2.4.0 regisztrált eszközének közvetlen kivételét a közös filter még típushelyesen megkapja; ez nem az SDK tetszőleges hibakonverziójának visszafejtése. Új SDK vagy egyedi belső adapter ezt a határt újraellenőrzendővé teszi.

A jelölt üzenet sem automatikusan titokmentes: a heurisztikus szűrő tetszőleges jelöletlen credentialt nem ismer fel. A fejlesztő ne jelöljön providerből másolt nyers üzenetet biztonságosnak. Az audit elő-/utóírási hibája továbbra is felülírja az eszköz eredményét a korábbi zárt hibával; a lezárási hiba nem visszagörgetés.

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

A `CompleteAsync` csak a megfelelő azonosítójú `Started` sort zárhatja `Succeeded`, `Failed` vagy `Cancelled` állapotba, nemnegatív, véges időtartammal és opcionális titokmentes hibával. Az időtartam a kezdőbejegyzés utáni végrehajtási szakaszt méri, a lezáró adatbázisírást nem. Ismételt vagy nem létező lezárás elutasított.

Lezárási hiba nem ad teljes sikert, és nem görgeti vissza a már lefutott eszközt: a sor `Started` állapotban marad. A kliens megszakítását az eszköz megkapja; a lezárás külön, öt másodperces határidejű tokent használ. Ez nem folyamatkilövési vagy azonnali natív I/O-megszakítási garancia.

Ismeretlen vagy hiányos metaadatú hívásnál nincs eszközvégrehajtás, de írható tárolónál `Failed` elutasítási audit készül. Az ismeretlen név fix marker, az argumentumok kimaradnak. Hiányzó/érvénytelen kockázatnál a `Forbidden` elutasítási besorolás, nem egy ismeretlen eszköz feltételezett üzleti kockázata. Érvényes kockázat hiányzó engedélyezőlistánál megmarad.

## Tároló API és kompatibilitás

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
