# #18 – közös MCP-auditkapu: átadás

## Eredmény és hatókör

- A `Program.cs` egyetlen SDK `AddCallToolFilter` regisztrációja fogja körül a regisztrált eszközök tényleges végrehajtását, a változatlan üzleti működésű `gate_ping`-et is.
- UTC kezdési idő, szerverkonfigurációból származó hívó, a felismert eszköz saját neve és explicit kockázata, szűrt paraméterek, GUID hívásazonosító, végállapot és `Stopwatch`-alapú monoton időtartam kerül az auditba. Az időtartam a kezdőbejegyzés tartósítása utáni végrehajtási szakaszt méri, a lezáró adatbázisírást nem.
- A `ToolAuditParametersAttribute` kötelező, a `gate_ping` listája explicit üres. Hiányzó vagy érvénytelen kockázat, hiányzó lista és ismeretlen eszköz esetén az eszközkód nem indul, de írható tárolónál ugyanazon közös kapu tartós `Started` → `Failed` elutasítási auditot készít. Ismeretlen kérésnév helyett fix `[ismeretlen-eszköz]` marker szerepel; minden elutasított kérés összes argumentumát kihagyjuk (`{}`). Hiányzó/érvénytelen üzleti kockázatnál a `Forbidden` kizárólag konzervatív **elutasítási besorolás**, nem az ismeretlen eszköz állítólagos valós üzleti kockázata. Hiányzó allowlistnél az egyébként érvényes deklarált kockázat megmarad. A biztonságos hibaszöveg jelzi az ismeretlen/hiányos metaadat miatti végrehajtástiltást.
- A naplózható neveket kizárólag a megbízható metadata-listából vesszük. A nem engedélyezett kérésneveket nem másoljuk át: egyetlen közös `"[kihagyva]":"[kihagyva]"` jelölő helyettesíti őket, így az ismeretlen nevek száma sem rekonstruálható. Objektum és tömb engedélyezett mezőben is `[kihagyva]`, hogy beágyazott nyers nevek se kerüljenek auditba. A skalár értékek szövegesen, a JSON null nullként tárolódik.
- Az engedélyezett skalárokon a meglévő `SecretRedactor.RedactText`, a megbízható mezőneveken annak `RedactParameters` ága fut. A konfigurált hívót is szűrjük. A kérés Authorization fejlécét nem olvassa az auditkapu.
- Kivételüzenet és MCP `IsError` eredménypayload soha nem kerül át az auditba vagy a visszaadott hibába: helyettük rögzített magyar hiba van, az audit hibaszövege a meglévő szűrőn is átmegy. A sikeres tool-payload változatlanul visszatér, de nem auditadat.

## Tartósság és hibaágak

- `IAuditStore.BeginAsync` sikeres befejezése előtt nincs eszközvégrehajtás. A SQLite-beszúrás saját implicit tranzakcióban tartósul.
- A tároló létrehozási/előírási hibája biztonságos MCP-eszközhibát eredményez; a számlálós tesztben nulla végrehajtás. A hibás adatbázis és a szándékosan hibázó fake külön le van fedve.
- A lezárás csak az azonos GUID-jú `Started` sort módosíthatja: `Succeeded`, `Failed` vagy `Cancelled` állapotba. Ismételt és nem létező hívás lezárása nem írhatja át az előzményt.
- Utólagos lezárási hibánál nincs teljes siker és nincs visszagörgetési ígéret. A SQLite-tesztben UPDATE-et megszakító trigger után a másik kapcsolattal újraolvasott kezdőbejegyzés `Started`, időtartama még null, miközben az eszköz már egyszer lefutott.
- A kliens megszakítását továbbadjuk az eszköznek; a lezárási kísérlet külön, öt másodperces tokennel történik. Ha az sem sikerül, a már tartós kezdőbejegyzés ellenőrzendő marad. Ez nem külső tranzakció és nem automatikus helyreállító folyamat.
- Kompatibilis új `AuditCalls` tábla készült. Az eredeti `AuditEntries` séma és sorai érintetlenek; a régi `AppendAsync`/`ReadAllAsync` API továbbra is a régi táblát kezeli. A régi sémát közvetlen SQL-lel létrehozó teszt új hívás lezárása után is mezőazonosan olvassa a régi sort. Új hívások közvetlenül az `AuditCalls` táblából olvashatók.

## Konfiguráció

```json
{
  "Audit": {
    "DatabasePath": "/var/lib/agentgate/audit.db",
    "Caller": "agent"
  }
}
```

Ezek az alapértékek; szokásos .NET konfigurációval, például `Audit__DatabasePath` és `Audit__Caller` környezeti kulcsokkal felülírhatók. A hívó szerveroldali konfiguráció, nem a kérés vagy a kliens önbevallása. A szülőkönyvtárnak léteznie és írhatónak kell lennie; a kód nem hoz létre telepítési könyvtárat. Hiánya biztonságos előírási hibát ad. A tesztfactory minden MCP-híváshoz külön ideiglenes adatbázist konfigurál, a teszt vége eltakarítja.

## Elsődleges SDK-forrás

A kutatás a rögzített **2.4.0** kiadásból történt, nem a változó main dokumentációból. Release tag: `v2.4.0`; commit: `4cbf42ef107763f0bc75c358af8cb2dfe47a769a`; közzététel: 2026-11-07T04:18:06Z.

- `https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.4.0`
- `https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.4.0/src/ModelContextProtocol.Core/Server/McpServerImpl.cs` – `ConfigureTools`, `BuildComposedCallToolHandler`: a primitive felismerése megelőzi a rendes filterláncot; a regisztrált tool is a filterbe kerül. A belső kivételt a saját filter fogja meg még az SDK hibakonverziója/hibanaplózása előtt.
- `https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.4.0/src/ModelContextProtocol/McpServerBuilderExtensions.cs` – `WithTools` és `WithRequestFilters` regisztráció.
- `https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.4.0/src/ModelContextProtocol.Core/Server/McpRequestFilters.cs` – rendes és alternate pipeline különbsége. Nem vezettünk be alternate handlert vagy Tasks-integrációt; az SDK tiltja az explicit alternate handler és rendes filter összekeverését.

| Rögzített SDK-forrás | Ellenőrzött naplózási pont |
| --- | --- |
| `McpServerImpl.cs` | 82, 142–150: közös logger; 1776–1790, 2550–2554: nyers toolnév lifecycle |
| `McpSessionHandler.cs` | 336–338, 826–847, 1248–1281: teljes üzenet Trace küldés/hibaág |
| `Protocol/TransportBase.cs` | 172–205, 238: nyers üzenetek és parsing-hibák |
| `Server/StreamableHttpServerTransport.cs`, `Server/StreamableHttpPostTransport.cs` | külön HTTP transportlogger és annak hibaágai |

A fenti további forrásokat ugyanarról a rögzített commitról olvastuk, nem a main ágról.

## SDK-naplózási védelem és tudatos veszteség

A filter önmagában nem elég: a 2.4.0 `McpServerImpl.LogToolCallLifecycles` a filter visszatérése után `request.Params.Name`-et ír Information szinten (`ToolCallCompleted`, illetve kivételnél `ToolCallError`). A teljes útvonal vizsgálata a HTTP handlerre, a sessionre és a transportokra is kiterjedt: a `McpServerImpl` a `McpServer` kategóriájú loggert adja a `McpSessionHandler`-nek; annak küldési/hibakezelési Trace ágai teljes JSON-üzenetet is szerializálnak. A transportoknak külön szerverkategóriáik vannak. Az általános `TransportBase` nyers bejövő/kimenő üzeneteket és parsing-hibákat is naplózhat; a jelenlegi HTTP transport saját loggerét is ellenőriztük. Ezért nem csak a filter utáni lifecycle eseményt kezeltük, és nem írjuk át a kérés eszköznevét.

A `Program.cs` `LoggerFilterOptions` utókonfigurációja kizárólag a `ModelContextProtocol.Server.` kategóriacsaládot tartalombiztosan letiltja minden szinten, minden normál `ILogger` provider számára. A meglévő szabályok eredeti szintje/predikátuma más kategóriákra változatlan; a provider-specifikus és pontosabb szerverkategória-felülírások sem kapcsolhatják vissza a nyers payloadot. Az alkalmazás és az ASP.NET naplózása megmarad, ezt külön probe állítások ellenőrzik. Az auth pipeline és a `gate_ping` üzleti viselkedése változatlan.

**Veszteség:** az SDK szerveroldali életciklus-, session- és transportdiagnosztikái (a Warning/Error szintűek is) nem jelennek meg a normál provider-naplóban. A tartós közös audit a toolhívások biztonságos kezdési/végállapot-, időtartam- és hibaadatait helyettesíti, nem minden alacsony szintű transportdiagnosztikát. Az SDK vésznaplóit nem kapcsoljuk vissza éles hibakeresés céljából; egy későbbi helyettesítő loggerhez külön tartalombiztonsági review szükséges.

**Határ:** az audit a közös SDK `tools/call` filterig eljutó hívásokat fedi, az ismeretlen és hiányos metadata miatti elutasításokat is. Auth előtt elutasított HTTP-kérés, JSON/protokoll-feldolgozáskor a filter előtt elbukó kérés, initialize/list és egyéb nem-tool protokollművelet nem `AuditCalls` toolhívás. Nem vezettünk be HTTP body loggingot vagy Activity/metric exportert; külső middleware, egy jövőbeli tool saját naplója, külön telemetry exporter vagy SDK-frissítés külön ellenőrzendő. A garancia a jelenlegi normál `ILogger` útvonalra és változatlan biztonsági regisztrációra szól, nem tetszőleges később hozzáadott `PostConfigure`/provider-kódra.

## Valódi RED → GREEN bizonyítékok

Minden új viselkedési szelet először elbukott, majd a minimális megvalósítás után átment. A kiegészítő regressziós tesztek a már lefedett viselkedést részletezik.

| Szelet | Megfigyelt RED |
| --- | --- |
| Valódi `gate_ping` + tartós közös audit | `no such table: AuditCalls` |
| Hiányzó allowlist | a várt hiba helyett siker |
| Allowlist, titokszűrés, nyers nevek/payload kihagyása | hiányzó auditparaméterkulcs |
| Tool-kivétel és `IsError` payload | `Started`, illetve `Succeeded` a várt `Failed` helyett |
| Előzetes írás hibája | a logikai szivárgásellenőrzés megbukott; a titok nem került a tesztkimenetbe |
| Utólagos írás hibája | ugyanez a lezárási hibaágon |
| Kliensoldali megszakítás | a véglegesítésre váró teszt timeouttal elbukott |
| Hibás kezdő- és végadatok | elmaradt várt argumentumkivétel; öt hibás végadat-kombináció |
| Konfigurált hívó szűrése | titokjelenlét logikai ellenőrzése megbukott |
| Túlméretes számérték szűrése | a várt teljes kitakarás hiányzott |
| Ismeretlen generált toolnév a normál SDK-naplóban | Information és Trace szinten is fix `A normál SDK-napló titkot tartalmaz.` hibával bukott; a biztonságos válasz és számláló=0 már előtte teljesült; javítás után 2/2 GREEN |
| Ismeretlen/hiányos metadata elutasítási auditja | mind a négy változatnál hiányzott az audit (`Pontosan egy elutasítási audit szükséges.`); javítás után a két fenti teszt együtt 6/6 GREEN |

A kiegészítő regressziók külön SQLite-kapcsolattal ellenőrzik mind a négy elutasítás tartósságát, a fix nevet/megmaradó vagy konzervatív kockázatot és az összes argumentum kihagyását. Az elutasítások előírási/lezárási hibája is nem-végrehajtó marad; lezárási hibánál `Started` és null időtartam látszik. Sikeres, generált payloadú valódi hívás Trace és provider-specifikus kategóriafelülírás mellett sem kerül a naplóba; a sikeres válasz változatlan, az alkalmazás-/ASP.NET-próbanaplók megmaradnak.

A szivárgástesztek csak futásidőben generált értékekkel dolgoznak, hibás állapotban sem írják ki azokat. A hibázó SDK/tárolótesztek szervernaplója memóriabeli tesztloggerbe kerül; a negatív állítások az auditot, a hibaválaszt és a normál szervernaplókat is vizsgálják.

## Futtatott ellenőrzések

A saját worktree-ben, `issue-18-mcp-audit` ágon:

- `dotnet restore AgentGate.sln` – sikeres. A friss worktree első `--no-restore` próbája még hiányzó assets fájl miatt nem tudott indulni; restore után nincs elakadás.
- Baseline `dotnet test AgentGate.sln --no-restore` – **199/199 sikeres**, minden modul, kihagyás nélkül.
- Szeletenként `dotnet test AgentGate.sln --no-restore --filter FullyQualifiedName~<tesztnév>` – a fenti RED/GREEN ciklusok tényleges futtatása.
- `dotnet build AgentGate.sln --no-restore` – **0 hiba, 0 figyelmeztetés**.
- `dotnet test AgentGate.sln --no-restore --logger 'trx;LogFileName=issue-18.trx' --results-directory TestResults/issue-18` – **229/229 sikeres**, 0 sikertelen, 0 kihagyott; a TRX számlálóit külön beolvasva is ellenőriztük. Bizonyíték: a gitből kizárt `TestResults/issue-18/issue-18.trx`.
- `dotnet format AgentGate.sln --verify-no-changes --no-restore` – sikeres. Az első ellenőrzés három új tesztsori whitespace-hibát jelzett; a formázás kizárólag az érintett saját fájlokra futott, majd a teljes ellenőrzés átment.
- `git diff --check` – sikeres.
- Review-javítás után `dotnet build AgentGate.sln --no-restore` – **0 hiba, 0 figyelmeztetés**.
- Review-javítás után `dotnet test AgentGate.sln --no-restore --logger 'trx;LogFileName=issue-18-review-fix.trx' --results-directory TestResults/issue-18-review-fix` – **241/241 sikeres**, 0 sikertelen, 0 kihagyott; a TRX számlálóit Python XML-parserrel külön ellenőriztük. A canaryk nem kerültek a tesztkimenetbe/TRX-be, csak a memóriabeli logger vizsgálta őket.
- Review-javítás után a teljes `dotnet format AgentGate.sln --verify-no-changes --no-restore`, `git diff --check` és `git diff --cached --check` sikeres. Ebben a javítási körben csak a `Program.cs`, `McpAuditFilter.cs`, `McpAuditTests.cs` és ez a dokumentum változott.
- A már baseline-ban is jelen lévő `VSTEST_ARTIFACTS_DIRECTORY ... falling back` tesztfuttató-üzenet megmaradt; nem új fordítási figyelmeztetés vagy teszthiba.

## Érintett fájlok

- `src/AgentGate/Audit/AuditCall.cs` – hívásmodell, tárolóinterfész, konfiguráció és allowlist-attribútum.
- `src/AgentGate/Audit/McpAuditFilter.cs` – közös kapu, titokmentes hibák és monoton mérés.
- `src/AgentGate/Audit/SqliteAuditStore.cs` – új kompatibilis tábla, Begin/Complete és adatellenőrzés.
- `src/AgentGate/Program.cs` – konfiguráció, DI, egyetlen SDK-filter.
- `src/AgentGate/Tools/GateTools.cs` – kizárólag explicit üres auditmetadata.
- `tests/AgentGate.Tests/Audit/McpAuditTests.cs` – valódi HTTP MCP-hívások SQLite-tal és fake audit-tárolóval.
- `tests/AgentGate.Tests/Audit/SqliteAuditStoreTests.cs` – régi sémás adatmegőrzés, új állapotgép, validálás, megszakítás.
- `tests/AgentGate.Tests/McpProtocolTests.cs` – meglévő initialize/list/ping állítások megtartása és valódi auditellenőrzés.
- `tests/AgentGate.Tests/McpTestApplication.cs` – ideiglenes auditkonfiguráció, bővíthető teszt-DI és takarítás.
- `tests/AgentGate.Tests/ToolRiskTests.cs` – minden deklarált és ténylegesen regisztrált tool kötelező auditmetadata-ellenőrzése.
- Ez az átadási dokumentum.

## Csomagindoklás, korlátok, további kérdés

**Új NuGet nincs.** A már rögzített MCP SDK 2.4.0 közös filtere és a meglévő Microsoft.Data.Sqlite elegendő; extra interceptáló, adatbázis-migrációs vagy mocking csomag nem indokolt.

A SecretRedactor heurisztikus: tetszőleges jelöletlen titkot nem ismer fel egy auditengedélyezett, ártalmatlannak nevezett szabad szövegben. Emiatt a jövőbeli toolok allowlistjére csak valóban auditálható mező kerülhet. A provider-kivételeket és MCP-hibapayloadokat nem próbáljuk ilyen módon megtisztítani, hanem teljesen lecseréljük. A sikeres tool-válasz titokmentessége továbbra is az eszköz feladata; az auditkapu nem másolja azt naplóba. A normál szervernaplók ellenőrzése nem állít tetszőleges jövőbeli trace/HTTP body logging konfigurációra teljes szivárgásmentességet.

A SQLite provider aszinkron API-jait használjuk és továbbadjuk a tokeneket, de a provider alatti I/O nem általános, kemény határidős megszakítási garancia. A lezárási token öt másodperces, nem külső művelet-visszagörgetési vagy processzkilövési határidő.

Nincs éles rendszer-, VM-, Home Assistant- vagy Bitwarden-hívás, valódi titok, új üzleti eszköz, telepítési módosítás. A #16 modul nincs használva, módosítva vagy bekötve. `.github/` és `AGENTS.md` változatlan. Nincs commit, push vagy PR. A szülőügynök független biztonsági és elfogadási ellenőrzése még szükséges; ez a dokumentum nem állít elvégzett független review-t. Megvalósítást blokkoló nyitott kérdés nincs.
