# Home Assistant olvasási kliens és MCP-eszközök

A főprogramba feltételesen bekötött kliens három olvasási művelete:

- `GetConfigAsync(ct)` → `GET /api/config`, szűrt `HomeAssistantConfig.Data` JSON objektum.
- `GetStatesAsync(ct)` → `GET /api/states`, az összes engedélyezett entitás `HomeAssistantState` DTO-ja.
- `GetStateAsync(entityId, ct)` → `GET /api/states/{entity_id}`, egy DTO.

Nincs általános HTTP-végpont, írás, titoknév szerinti keresés vagy közvetlen Bitwarden-hívás.

## Regisztráció és konfiguráció

A főprogram meghívja az `AddHomeAssistantClient(IServiceCollection, IConfiguration)` bővítést. Teljesen hiányzó `HomeAssistant` szakasznál nincs kliens, HA-options-validálás vagy HTTP-figyelmeztető szolgáltatás. Megadott üres/null/skalár/hiányos/hibás szakasz fix „A Home Assistant konfiguráció érvénytelen.” indulási hibát ad, érték-visszhang nélkül. A megadott szakasz `ValidateOnStart` ellenőrzést kap. Az `ISecretProvider` regisztrációja a befogadó alkalmazás feladata. Induláskor nincs HTTP és nincs titoklekérés. A beállítások indulási pillanatképet alkotnak, nincs élő újratöltés.

Lásd az `appsettings.example.jsonc` megjegyzéses mintát. A GUID eszközzel generált, nem valódi titokhivatkozás; nem tartalmaz tokent. A `TokenSecretId` a biztonságos hibakezelés miatt szöveges opció, de csak nem üres GUID fogadható el, és a szolgáltató már `Guid` értéket és `CancellationToken`-t kap. Hibás/hiányzó/üres GUID fix üzenetű `OptionsValidationException`-t okoz. A kézi konstruktor is ellenőriz.

- `BaseUrl`: alapból kizárólag HTTPS origin, root útvonallal; nincs userinfo, query, fragment vagy útvonalprefix. A tanúsítvány-ellenőrzést nem kerüljük meg. HTTPS esetén az alábbi HTTP-kivétel nem változtatja meg a korábbi címelfogadást.
- `AllowHttp`: alapérték **false**. Csak explicit `true` mellett engedélyezett HTTP, kizárólag kanonikus, négyrészes decimális IPv4-literállal a `10.0.0.0/8`, `172.16.0.0/12` vagy `192.168.0.0/16` tartományban (például `http://192.168.1.2:8123/`). Hostnévhez nincs kivétel és nincs DNS-feloldás; public, loopback, link-local, CGNAT, IPv6 és IPv4-mapped IPv6 HTTP-cím tiltott. Rövidített, hexadecimális, oktális vagy vezető nullás IPv4-alak sem megengedett. A többi origin-korlát HTTP-n is változatlan. Hibás boolean szöveg fix konfigurációs hibával megállítja az indulást, HTTPS mellett is.
- **Titkosítatlan kapcsolat kockázata:** HTTP-n a Bearer token és a forgalom lehallgatható/módosítható; a privát IP önmagában nem ad titkosítást vagy szerverhitelesítést. Lehetőleg HTTPS-t használj. Az extension által regisztrált `IHostedService` engedélyezett `AllowHttp` esetén induláskor konstans magyar `Warning` üzenetet naplóz, HTTPS alapcím mellett is, URL, token és titokazonosító nélkül. `false` vagy hiányzó flag esetén nincs ilyen figyelmeztetés; a közvetlen konstruktor nem indít hostszolgáltatást.
- `DomainAllowlist`: pontos, kisbetűs ASCII domainnevek; nincs wildcard. Hiányzó vagy üres lista **deny all**. Egyedi tiltott/malformed entitásnál még titoklekérés sem történik; listából minden ilyen entitás kimarad.
- `Timeout`: alapérték 10 másodperc, pozitív és legfeljebb 5 perc; a titoklekérést, HTTP-t és a választest olvasását is lefedi. Hibás szöveges időkorlát is fix biztonságos konfigurációs hibát ad.
- Entitásforma: `\A[a-z0-9_]+\.[a-z0-9_]+\z`, legfeljebb 255 karakter. Unicode, útvonalelválasztó, query, fragment, százalékos kódolás, whitespace nem megengedett. Az egyedi válasz azonosítójának egyeznie kell a kérttel.

Új konfigurációs kulcs nincs. A meglévő pontos nevek: `HomeAssistant:BaseUrl`, `HomeAssistant:AllowHttp`, `HomeAssistant:TokenSecretId`, `HomeAssistant:DomainAllowlist`, `HomeAssistant:Timeout`; szerepük fent olvasható. A mintában pontosan `light`, `switch`, `sensor`, `binary_sensor`, `climate`, `cover`, `weather`, `sun`, `scene`, `input_boolean`, `person`, `zone` szerepel, ebben a sorrendben; nincs `device_tracker`. Ez csak minta, nem automatikus vagy éles engedélyezés.

A `HomeAssistant:TokenSecretId` utalhat ugyanarra a Bitwarden-titokra, mint a különálló `HomeAssistantApproval:TokenSecretId`. Nincs titokmásolás vagy inline token: a tokent kizárólag a meglévő `ISecretProvider` kéri le futásidőben. Az olvasási modul hiánya nem kapcsolja ki a jóváhagyási csatorna vagy a titokönteszt saját beállításait.

## MCP-szerződés

Mindhárom eszköz mindig regisztrált, explicit Read kockázattal és read-only jelöléssel, a közös tartós auditkapun át. Hiányzó kliensnél fix `GateToolException`: „A Home Assistant nincs beállítva.” Hibás konfigurációt nem álcázunk hiánynak. A provider-/HTTP-/válaszhiba és timeout toolszinten fix „A Home Assistant lekérés sikertelen.”; a hívói megszakítás továbbadódik.

- `ha_list_states(domain?)`: az engedélyezőlista és az opcionális pontos domain metszete, majd a helyadatgyanús entitások kihagyása, ordinális `entity_id` rendezés és **rögzített 100-as limit**. A válasz `items`, `returned_count`, `truncated`; utóbbi pontosan 100-nál több kiadható találatnál igaz. Csonkoláskor szűkíts domain szerint; nincs lapozás vagy limitkonfiguráció. Tiltott/hibás domainnél nincs titok- vagy HTTP-hívás.
- Listaelem: csak `entity_id`, `state`, opcionális string `friendly_name`, `unit_of_measurement`, valamint opcionális `last_changed`. Az utóbbi a HA-válasz ellenőrzött ISO-időbélyege (teljes dátum+idő+másodperc, opcionális 1–7 törtmásodperc, Z vagy numerikus offset), nem a lekérés ideje; hiányzó/hibás értéket nem pótolunk.
- `ha_get_state(entity_id)`: ugyanezek a mezők és `attributes`. Csak öt pontos nevű, string típusú attribútum adható ki: `friendly_name`, `unit_of_measurement`, `device_class`, `state_class`, `icon`. Ismeretlen mező, objektum, tömb, szám, boolean, null kimarad. Tiltott/hibás azonosítónál nincs I/O; helyadatgyanús válasznál fix elutasítás van.
- `ha_get_config`: csak string `version`, `time_zone`, `language`, `location_name` és a szűrt `unit_system` objektum. Utóbbiban csak string `length`, `mass`, `temperature`, `volume`, `pressure`, `wind_speed`, `accumulated_precipitation` engedélyezett. Hiányzó/rossz típusú érték kimarad; üres egységrendszer megmaradhat. Koordináta, elevation, URL, könyvtárlista, komponenslista és minden más mező kizárt.

Csak a tényleges bemenetnevek auditálhatók: a listánál `domain`, az egyedi olvasásnál `entity_id`, a confignál egyik sem. Nincs válasz, nyers attribútum vagy konfiguráció az auditparaméterekben. A strukturált MCP-válasz közvetlenül a fenti objektum; a szöveges tartalom ugyanennek a JSON-ábrázolása.

### Helyadatkiadási határ

Minden domainre, így `person` és `zone` entitásokra is ugyanaz a zárt projekció vonatkozik. Az ismeretlen attribútumokat teljes értékükkel eldobjuk, így azok beágyazott objektumai/tömbjei sem kerülnek ki. Nincs általános attribútumfa-visszaadás.

A helyadatgyanús nevek vizsgálata csak ASCII betű/szám megtartásával és invariant kisbetűsítéssel történik. A normalizált névben bárhol szereplő `latitude`, `longitude`, `gps`, `coordinate`, `altitude`, `elevation`, `course`, `speed`, `radius` tiltó jelzés (prefix/suffix esetén is). Példák: `GPS_accuracy`, `gps-accuracy`, `GpsAccuracy` → `gpsaccuracy`. Az attribútum pozitív listáján egyébként sem szerepelnek ezek; `wind_speed` mérési attribútum is kimarad. A config `unit_system.wind_speed` string egységcímkéje viszont megengedett, mert nem helymérés.

Entitásazonosítóban helyadatgyanús név, illetve a `state` vagy az öt engedélyezett szöveges attribútum bármelyikében felismert helyadatjelzés az egész entitást kizárja: listából kimarad, részletes olvasásnál „Az entitás helyadatgyanús adatot tartalmaz.”. Vizsgált alakok: a fenti normalizált névrészletek, kis-/nagybetűfüggetlen `geo:`, számot követő fokjel (DMS-alak is), két előjeles vagy előjel nélküli, legfeljebb három egészjegyű szám vessző/pontosvessző/perjel elválasztóval, opcionális ponttal írt törtrésszel és whitespace-szel. Példák: `47.123, 19.456`, `geo:47.123;19.456`, `latitude=47.123 longitude=19.456`, `47° 12′ 3″ N 19° 4′ 5″ E`. A tizedesponttal írt számpár whitespace-elválasztással is tiltott; a szám előtti/utáni `N`, `S`, `E`, `W` irányjel és a `lat`, `lon`, `lng` címke kettősponttal vagy egyenlőségjellel szintén jelzés (kis-/nagybetűfüggetlenül). Tesztelt példák: `+47.123 -19.456`, `N 47.123 E 19.456`, `47.123N 19.456E`, `lat=47.123 lon=19.456`. A `unit_of_measurement` pontos `°`, `deg`, `degree`, `degrees`, `rad` címkéje is kizáró jelzés (trim és kisbetűsítés után). Ez a név/state/friendly_name/unit együttes ellenőrzése, nem kizárólag kulcsszűrés. Config engedélyezett szövegmezőjéből a felismert helyadatgyanús érték kimarad.

A szűrés konzervatív: ártalmatlan név vagy szöveg is kieshet (például speed nevű szenzor). **Nem garantál tetszőleges ismeretlen elnevezésű, kódolt vagy obfuszkált szabad szöveg szemantikus GPS-felismerését**; önmagában egy jelöletlen szám nem bizonyítható koordinátának. Új adatmodell/formátum csak külön tesztelt nem-helyadat szerződéssel bővítheti a pozitív listát.

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
