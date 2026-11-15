# Home Assistant olvasási kliens

Önálló modul, MCP-eszköz és főprogrambekötés nélkül. Pontosan három publikus olvasás:

- `GetConfigAsync(ct)` → `GET /api/config`, szűrt `HomeAssistantConfig.Data` JSON objektum.
- `GetStatesAsync(ct)` → `GET /api/states`, az összes engedélyezett entitás `HomeAssistantState` DTO-ja.
- `GetStateAsync(entityId, ct)` → `GET /api/states/{entity_id}`, egy DTO.

Nincs általános HTTP-végpont, írás, titoknév szerinti keresés vagy közvetlen Bitwarden-hívás.

## Regisztráció és konfiguráció

A modul `AddHomeAssistantClient(IServiceCollection, IConfiguration)` bővítése regisztrálja a klienst és a `HomeAssistant` szakasz beállításait, `ValidateOnStart` ellenőrzéssel. Az `ISecretProvider` regisztrációja a befogadó alkalmazás feladata. Induláskor nincs HTTP és nincs titoklekérés. A beállítások indulási pillanatképet alkotnak, nincs élő újratöltés.

Lásd az `appsettings.example.jsonc` megjegyzéses mintát. A GUID eszközzel generált, nem valódi titokhivatkozás; nem tartalmaz tokent. A `TokenSecretId` a biztonságos hibakezelés miatt szöveges opció, de csak nem üres GUID fogadható el, és a szolgáltató már `Guid` értéket és `CancellationToken`-t kap. Hibás/hiányzó/üres GUID fix üzenetű `OptionsValidationException`-t okoz. A kézi konstruktor is ellenőriz.

- `BaseUrl`: alapból kizárólag HTTPS origin, root útvonallal; nincs userinfo, query, fragment vagy útvonalprefix. A tanúsítvány-ellenőrzést nem kerüljük meg. HTTPS esetén az alábbi HTTP-kivétel nem változtatja meg a korábbi címelfogadást.
- `AllowHttp`: alapérték **false**. Csak explicit `true` mellett engedélyezett HTTP, kizárólag kanonikus, négyrészes decimális IPv4-literállal a `10.0.0.0/8`, `172.16.0.0/12` vagy `192.168.0.0/16` tartományban (például `http://192.168.1.2:8123/`). Hostnévhez nincs kivétel és nincs DNS-feloldás; public, loopback, link-local, CGNAT, IPv6 és IPv4-mapped IPv6 HTTP-cím tiltott. Rövidített, hexadecimális, oktális vagy vezető nullás IPv4-alak sem megengedett. A többi origin-korlát HTTP-n is változatlan. Hibás boolean szöveg fix konfigurációs hibával megállítja az indulást, HTTPS mellett is.
- **Titkosítatlan kapcsolat kockázata:** HTTP-n a Bearer token és a forgalom lehallgatható/módosítható; a privát IP önmagában nem ad titkosítást vagy szerverhitelesítést. Lehetőleg HTTPS-t használj. Az extension által regisztrált `IHostedService` engedélyezett `AllowHttp` esetén induláskor konstans magyar `Warning` üzenetet naplóz, HTTPS alapcím mellett is, URL, token és titokazonosító nélkül. `false` vagy hiányzó flag esetén nincs ilyen figyelmeztetés; a közvetlen konstruktor nem indít hostszolgáltatást.
- `DomainAllowlist`: pontos, kisbetűs ASCII domainnevek; nincs wildcard. Hiányzó vagy üres lista **deny all**. Egyedi tiltott/malformed entitásnál még titoklekérés sem történik; listából minden ilyen entitás kimarad.
- `Timeout`: alapérték 10 másodperc, pozitív és legfeljebb 5 perc; a titoklekérést, HTTP-t és a választest olvasását is lefedi. Hibás szöveges időkorlát is fix biztonságos konfigurációs hibát ad.
- Entitásforma: `\A[a-z0-9_]+\.[a-z0-9_]+\z`, legfeljebb 255 karakter. Unicode, útvonalelválasztó, query, fragment, százalékos kódolás, whitespace nem megengedett. Az egyedi válasz azonosítójának egyeznie kell a kérttel.

A bővítő metódust a befogadó alkalmazásnak kell meghívnia. A modul nem köti be magát automatikusan a főprogramba, és nem regisztrál MCP-eszközt vagy vészleállítót.

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
