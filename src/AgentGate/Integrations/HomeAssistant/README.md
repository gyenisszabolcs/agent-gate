# Home Assistant olvasási kliens (#20)

Önálló modul, MCP-eszköz és főprogrambekötés nélkül. Pontosan három publikus olvasás:

- `GetConfigAsync(ct)` → `GET /api/config`, szűrt `HomeAssistantConfig.Data` JSON objektum.
- `GetStatesAsync(ct)` → `GET /api/states`, az összes engedélyezett entitás `HomeAssistantState` DTO-ja.
- `GetStateAsync(entityId, ct)` → `GET /api/states/{entity_id}`, egy DTO.

Nincs általános HTTP-végpont, írás, titoknév szerinti keresés vagy közvetlen Bitwarden-hívás.

## Regisztráció és konfiguráció

A modul `AddHomeAssistantClient(IServiceCollection, IConfiguration)` bővítése regisztrálja a klienst és a `HomeAssistant` szakasz beállításait, `ValidateOnStart` ellenőrzéssel. Az `ISecretProvider` regisztrációja a befogadó alkalmazás feladata. Induláskor nincs HTTP és nincs titoklekérés. A beállítások indulási pillanatképet alkotnak, nincs élő újratöltés.

Lásd az `appsettings.example.jsonc` megjegyzéses mintát. A GUID eszközzel generált, nem valódi titokhivatkozás; nem tartalmaz tokent. A `TokenSecretId` a biztonságos hibakezelés miatt szöveges opció, de csak nem üres GUID fogadható el, és a szolgáltató már `Guid` értéket és `CancellationToken`-t kap. Hibás/hiányzó/üres GUID fix üzenetű `OptionsValidationException`-t okoz. A kézi konstruktor is ellenőriz.

- `BaseUrl`: kizárólag HTTPS origin, root útvonallal; nincs userinfo, query, fragment vagy útvonalprefix. A tanúsítvány-ellenőrzést nem kerüljük meg. A sima HTTP szándékosan nem támogatott, a Bearer token miatt.
- `DomainAllowlist`: pontos, kisbetűs ASCII domainnevek; nincs wildcard. Hiányzó vagy üres lista **deny all**. Egyedi tiltott/malformed entitásnál még titoklekérés sem történik; listából minden ilyen entitás kimarad.
- `Timeout`: alapérték 10 másodperc, pozitív és legfeljebb 5 perc; a titoklekérést, HTTP-t és a választest olvasását is lefedi. Hibás szöveges időkorlát is fix biztonságos konfigurációs hibát ad.
- Entitásforma: `\A[a-z0-9_]+\.[a-z0-9_]+\z`, legfeljebb 255 karakter. Unicode, útvonalelválasztó, query, fragment, százalékos kódolás, whitespace nem megengedett. Az egyedi válasz azonosítójának egyeznie kell a kérttel.

A `Program.cs` kifejezetten változatlan. A valódi gate bekötése a #18 merge után külön kis feladat lesz; ez tudatos későbbi scope, nem a modul blokkolója. A #16 vészleállítót ez a modul nem használja és nem módosítja.

## Titok- és hálózati határok

- A beépített `SocketsHttpHandler` nem követ átirányítást (azonos originre sem), nem használ cookie-t vagy környezeti proxyt. A 3xx hibát jelent. Nincs retry vagy tokenes default fejléc.
- A klienst saját `HttpClient` szolgálja ki, `IHttpClientFactory` kérésnaplózó pipeline nélkül. A modul nem naplóz URL-t, fejlécet, választ vagy kivételt. Kizárólag az aktuális kérés Authorization fejlécébe kerül a token.
- A háromparaméteres konstruktor handler-injektálási pontja **megbízható infrastruktúra**, tesztben fake handler. Éles használatban az extension/kétparaméteres konstruktor az előírt út; tetszőleges külső handler saját átirányítása/naplózása nem tehető biztonságossá e modulból. Külső HTTP tracing/diagnosztika se rögzítsen hitelesítési adatot.
- Minden HTTP-, JSON-, titokszolgáltatói és fejlécformázási hiba fix magyar üzenetté alakul, belső kivétel nélkül. A hívói megszakítás biztonságos `OperationCanceledException` az eredeti hívói CT-vel, a timeout külön fix hiba.
- A választest legfeljebb 1 MiB, a JSON legfeljebb 32 mély. Túllépéskor teljes hiba van, nem csonkolt állapotlista. A DTO csak a szükséges mezőket adja ki: entity ID, állapot, szűrt attribútumobjektum; nem ad vissza HTTP-választ/fejlécet/nyers testet.

### A JSON-szűrés pontos érvényességi határa

A `HomeAssistantJsonSanitizer` külön strukturális JSON-szűrő; **nem** a `SecretRedactor` szövegheurisztikájára támaszkodik. Minden objektum- és tömbszinten eltávolítja a normalizált nevükben `token`, `secret`, `password`, `passwd`, `authorization`, `apikey`, `credential`, `cookie`, `privatekey` vagy `entitypicture` részt tartalmazó tulajdonságokat. Így az `entity_picture` egészben kiesik, a kameratoken sem marad meg benne. A tényleges Bearer token szó szerinti előfordulását a szövegértékekben takarja, az azt tartalmazó tulajdonságnevet eltávolítja.

Konzervatívan egészben takar **minden** `?`, `#` vagy `@` karaktert tartalmazó szövegértéket. Így a szokásos abszolút/relatív URL-ek query tokenje, kódolt query-kulcsa, userinfo része és fragmentje nem kerül ki; ennek ára, hogy ártalmatlan query URL, e-mail vagy ilyen írásjelű szöveg is elveszhet.

Ez nem általános DLP-garancia: ismeretlen nevű szabad szövegben, más kódolásban, bináris adatban vagy számértékben rejtett ismeretlen titkot nem tud megbízhatóan felismerni. Az engedélyezett HA-domainek és integrációk adatmodellje továbbra is bizalmi határ; új érzékeny attribútumnév új szűrési szabályt és tesztet igényel.

## Ellenőrzés és átadás

A tesztek csak fake `HttpMessageHandler`-t és fake `ISecretProvider`-t használnak; nincs valódi Home Assistant/Bitwarden elérés. A teszttitkok futásidőben generáltak, az érzékeny ellenőrzések boolean assertionnel és fix hibaüzenettel működnek, nem írják ki a titkot. Izolált hostteszt **ugyanazt** az extensiont használja hibás/üres GUID és érvényes GUID esetén.

TDD RED → GREEN: hiányzó config API; domain- és entitásvédelem/listázás; sikeres JSON strukturális titokmentesítése; startup extension; biztonságos hiba/timeout/megszakítás. A RED lépések ténylegesen futtatva és a megfelelő hiányzó viselkedésen buktak.

Parancsok a repository gyökeréből:

```sh
dotnet build AgentGate.sln --no-restore --nologo
dotnet test AgentGate.sln --no-restore --nologo
dotnet test --no-restore --filter FullyQualifiedName~HomeAssistant --nologo
dotnet format AgentGate.sln --no-restore --verify-no-changes
git diff --cached --check
```

Új NuGet-függőség nincs. Ellenőrzött eredmény: baseline **199/199**, teljes végső tesztfutás **262/262**, ebből új HA-teszt **63/63**, kihagyott teszt nincs. A build **0 warning / 0 error**, a teljes `dotnet format --verify-no-changes` sikeres. A saját módosítások új fájlok: `HomeAssistantClient.cs`, `HomeAssistantOptions.cs`, `HomeAssistantModels.cs`, `HomeAssistantJsonSanitizer.cs`, `HomeAssistantServiceCollectionExtensions.cs`, e README és a JSONC-minta a modulban, továbbá `tests/AgentGate.Tests/HomeAssistantClientTests.cs` és `HomeAssistantRegistrationTests.cs`.

Nyitott modulkérdés nincs. A HTTPS-only és a konzervatív URL-szövegtakarás tudatos biztonsági korlát; a független review ezeket is ellenőrizze. Commit/push/PR nincs; az átvevő végzi a független biztonsági felülvizsgálatot és a PR-t. A meglévő teszt SDK `VSTEST_ARTIFACTS_DIRECTORY` fallback tájékoztatása már a baseline futásban is megjelent, nem új fordítási figyelmeztetés.
