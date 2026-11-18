# Bitwarden Secrets Manager titoklekérés

## Használat

A titkot az `ISecretProvider.GetSecretAsync(Guid, CancellationToken)` interfészen keresztül kell lekérni. Az azonosító nem lehet üres. A visszaadott `string` bizalmas adat: nem naplózható és nem adható MCP-válaszba. A menedzselt string memóriájának garantált nullázását a modul nem ígéri.

A `BitwardenSecretProvider` minden nyers lekéréskor futásidőben választ tokenforrást. A `Program.cs` singleton `ISecretProvider` példánya a `CachedSecretProvider` dekorátor; az alatta levő nyers provider `credentialOnly: true` beállítással működik: a gate **kizárólag systemd credentialt** fogad el, hiányzó `CREDENTIALS_DIRECTORY` mellett sem olvassa az env-tokent. A konstruktor alapértelmezett `credentialOnly: false` értéke a korábbi, külön példányosított fejlesztői használattal forráskompatibilis:

- Ha a `CREDENTIALS_DIRECTORY` nincs beállítva (null), az `AGENT_GATE_BITWARDEN_ACCESS_TOKEN` fejlesztői környezeti változót olvassa.
- Bármely nem-null `CREDENTIALS_DIRECTORY` kizárólagos credentialmódot jelent. Hibás, üres, nem létező vagy nem olvasható credential esetén nincs visszaesés az env-tokenre, és azt nem is olvassa ki.
- A credentialfájlnév a konstruktor `credentialFileName` argumentuma; alapértéke `bitwarden-access-token`.
- Mindkét forráson csak a záró whitespace kerül levágásra (`TrimEnd`); az eleje változatlan. Az ezután üres token elutasított.

```csharp
ISecretProvider provider = new BitwardenSecretProvider(
    credentialFileName: "bitwarden-access-token");
```

A környezetolvasó és az `IBitwardenSecretClient` megbízható, helyettesíthető függőség. Éles használatban a környezetolvasónak a tényleges folyamatkörnyezetet kell tükröznie. Az `ISecretProvider` GUID-alapú API-ja nem végez név szerinti keresést.

## Memóriabeli cache és visszavonás

- Titokazonosítónként külön bejegyzés, a sikeres nyers lekérés befejezésétől mért fix **5 perc**, monoton `TimeProvider` időbélyeggel. A pontos 5 perces határon már frissítés kell; a találatok nem hosszabbítanak. Hiba és üres eredmény nem cache-elhető, lejárt érték nem tartalék.
- A normál azonos kulcsú várakozók egy közös lekérést osztanak meg; más kulcs közben haladhat. A nyers munka saját alkalmazási megszakítási tokent kap, nem az első hívóét. A hívói cancellation/HA-határidő csak a várakozást szakítja meg: a munka később melegítheti a cache-t, ha nem vontuk vissza és az alkalmazás nem állt le. Minden várakozó távozása után is tulajdonolt marad, a késői hiba megfigyelt és szanitizált.
- `GetSecretLeaseAsync` ugyanahhoz a kiadható generációhoz ugyanazt a `SecretLease` objektumot adja. A `Value` bizalmas string, nem naplózható/serializálható; a `ToString()` csak rögzített jelölés. A HA-kliens a használt referenciát adja vissza az `Invalidate(id, lease)` metódusnak HTTP 401 esetén. **Nincs HTTP retry, POST-ismétlés sem**. A következő külön kérés frissít; késői régi 401 azonos titokszöveg mellett sem törölheti az új generációt vagy a már futó frissítést. Más HTTP hibakód nem von vissza.
- A konkrét dekorátor szerveroldali `Invalidate(id)` metódusa a futó generációt is visszavonja. A már futó nyers hívást megvárjuk (nincs párhuzamos póthívás); eredménye nem tölthet cache-t és nem adható ki. A még várakozó normál hívók ezután közösen frissítenek. Egy már HTTP-hoz átadott titok nem hívható vissza a hálózatról.
- `GetFreshSecretAsync` külön nyers lekérés, nem használja és nem tölti a cache-t; ez az önteszt útja. Szándékosan nem csatlakozik normál cache-flight-hoz. Az interfész alapértelmezett metódusai a cache nélküli providerekkel és meglévő helyettesítőkkel kompatibilisek. Új MCP-eszköz és konfigurációs kulcs **nincs**.
- A dekorátor ugyanaz a singleton az `ISecretProvider` és az `IHostedService` regisztrációban. A szinkron natív belépést külön tulajdonolt munkafeladat kezeli; a futó SDK-hívást nem tudjuk megszakítani. `StopAsync` lezárja az új munkát, üríti a bejegyzéseket, megszakítást jelez és a host tokenjéig vár. A később befejeződő munka továbbra is megfigyelt, eredménye nem kiadható. `DisposeAsync` tényleges befejezésig vár, ezért beragadt natív hívás a végső felszabadítást késleltetheti; nincs kitalált natív timeout-garancia. A friss önteszt várakozásának megszakítása után is a cache-életciklus birtokolja a nyers munkát.
- Csak folyamatmemória, nincs SDK-session- vagy lemezes cache. A TTL **kiadhatósági határ**, nem fizikai RAM-törlés: lejárt stringet a következő hozzáférés/ürítés enged el, a már átadott referenciák és a GC életideje nem garantálható. Visszavonás nélkül egy rotált/visszavont titok legfeljebb a TTL-ig újrahasználható; a HA 401 célzottan rövidíti ezt.

## Megismételhető mockmérés

`dotnet test -c Release --verbosity quiet --filter FullyQualifiedName~SecretCacheMeasurementTests --logger 'console;verbosity=detailed'`

A mérés nem éles benchmark. A `ha_get_state`, `ha_control` és `ha_list_areas` azonos HA-kliensútjait futtatja (a control esetén a service POST-transportot, nem teljes MCP-/auditútvonalat). A provider `Task.Delay(150 ms)`, a HTTP-kezelő `Task.Delay(5 ms)` késleltetésű, fázisonként és műveletenként 3 ismétlés, tényleges `Stopwatch` idők és lekérésszámok. Fázisok: cache nélkül, minden ismétlés előtt kényszerített hideg miss, meleg cache, minden ismétlés előtt virtuálisan 5 perccel lejárt cache, 401 és az utána indított külön kérés együtt. Az utolsó fázis 2 HTTP-hívást mér ismétlésenként, nem retry-t. A virtuális óra csak a TTL-t lépteti, a közölt milliszekundumok valódi futásból származnak.

A TTS-proof ugyanazt, a monoton minimumellenőrzéssel szigorított `HomeAssistantControlService` implementációt futtatja cache nélküli és előmelegített úton, útvonalanként 1 mintával, 1100 ms-os mock providerrel és azonnali mock HTTP-val. A harmadik GET igazolja a 0,4 hangerőt, előtte 0,9-et látunk. A tényleges 3 másodperces ablak és legalább 500 ms GET-köz megmarad; a melegítés idejét külön közli. A cache nélküli kontroll időtúllépést és nulla speech POST-ot vár. Szálütemezés miatt az ablakba férő kontroll-GET-ek száma változhat; a teszt nem éles SLA. Hideg/lejárt/401 utáni natív lekérésre **nincs 3 másodperces ígéret**.

## Induláskori, opcionális önteszt

A `Program.cs` regisztrálja a `SecretSelfTestService` háttérszolgáltatást. A `Secrets:SelfTestSecretId` elhagyása teljesen kikapcsolja az öntesztet: az önteszt nem old fel szolgáltatót, nincs tokenolvasás, SDK-hívás vagy öntesztnapló (a cache-életciklus regisztrációja önmagában sem olvas titkot). Üres, hibás vagy csupa nulla GUID rögzített `Configuration` hibát naplóz, az érték visszaírása nélkül. Érvényes, nem üres GUID esetén indulásonként egyszer frissen olvas a `GetFreshSecretAsync` úton, a háttérben; nincs új MCP-eszköz vagy Home Assistant-regisztráció.

- A sikerüzenet: `A Bitwarden önteszt sikeres.`
- A hibaüzenet: `A Bitwarden önteszt sikertelen. Hibatípus: ….` Az egyetlen változó mező az engedélyezett `ErrorType`: `MissingAccessToken`, `NotFound`, `AuthenticationFailed`, `ProviderFailure`, `UnsafeDiagnostics`; a konfigurációhiba és az időtúllépés rögzített típusa `Configuration`, illetve `Timeout`.
- Nincs titokérték, hossz, GUID, token, nyers kivétel vagy szolgáltatói diagnosztika; a kivétel `Data`, `Source` és belső részletei sem kerülnek a naplóba. Az önteszt nem nyit naplóscope-ot.
- A jelzési határidő **5 másodperc**. Lejáratkor azonnal biztonságos időtúllépési napló keletkezik, majd a szolgáltatás megszakítást jelez a saját munkájának. A gate indulását és `/health` elérését sem lassú, sem szinkron belépésű szolgáltató nem blokkolja; az önteszthiba nem állítja le a hostot.
- Ez **nem natív átviteli időkorlát**. A szinkron Bitwarden SDK futó hálózati hívása nem állítható le erőszakkal. A háttérszolgáltatás megőrzi és megvárja a munkafeladatot, megfigyeli a késői hibát, és nem ad ki késői titkot vagy második sikerjelzést.
- Leállításkor szintén megszakítást jelez és várja a felszabadítást. A host saját `StopAsync` határideje ettől függetlenül korlátozza a leállítási várakozást; a munka az `ExecuteTask` része marad, amíg ténylegesen vissza nem tér. A folyamat végleges kilépése megszakíthatja ezt a várakozást; végtelenül beragadt natív hívás takarítására nincs garancia.

Az önteszt nem üzemkészségi feltétel és nem ismétlődő állapotellenőrzés. A tesztek helyettesített szolgáltatókkal futnak, éles Bitwarden-hívás nélkül.

## Credentialfájl és bizalmi határ

- A fájlnév csak ASCII betűt, számot, pontot, aláhúzást és kötőjelet tartalmazhat; nem lehet üres, `.` vagy `..`. Abszolút útvonal, alkönyvtár, perjel, meghajtójelölés vagy whitespace tiltott.
- A credentialgyökér létező, abszolút könyvtár legyen. Az üres/whitespace, relatív, nem létező vagy fájlra mutató gyökér hibás beállítás, nem hiányzó beállítás.
- A közvetlen gyökér- és fájlsymlink/reparse point elutasított. A hiányzó, nem olvasható vagy könyvtárként létező credential `ProviderFailure`; az üres token `MissingAccessToken` hibát ad.
- A fájl aszinkron olvasása megkapja a nyers lekérés `CancellationToken` értékét. Nincs `.env`, lemezes titokgyorsítótár vagy automatikus credentialtelepítés.
- A systemd által biztosított gyökérnek és őseinek megbízhatónak, nem módosíthatónak kell lenniük. A metaadat-ellenőrzés és megnyitás nem atomi `openat`/`O_NOFOLLOW` védelem támadó által írható könyvtár ellen. A modul nem ellenőrzi vagy állítja át a mountot és a tulajdonost.

## Kötelező SDK-naplózásvédelem

A folyamatot **`RUST_LOG=off`** környezettel kell indítani. Ezt futás közben nem szabad megváltoztatni, és más kód sem inicializálhatja előbb az SDK-t eltérő beállítással. A natív SDK debug naplója hitelesítési adatot tartalmazhat; az adapter ezért minden nem pontosan `off` értéknél még a natív inicializálás előtt elutasít.

Az adapter nem módosítja globálisan a környezetet és nem irányítja át a stderr-t. A befogadó alkalmazás feladata a DI- és üzemeltetési beállítások biztosítása.

## Hibák és korlátok

- Minden kifelé adott hiba rögzített magyar üzenetű. Nyers szolgáltatói/fájlrendszeri kivétel, útvonal, fájltartalom, `InnerException` vagy `Data` nem jut át a határon.
- A hibakódok: `MissingAccessToken`, `NotFound`, `AuthenticationFailed`, `ProviderFailure`, `UnsafeDiagnostics`. A nem található és a gépi fióknak nem hozzáférhető titok nem különböztethető meg biztosan.
- Megszakításkor új, biztonságos `OperationCanceledException` keletkezik az eredeti hívói tokennel. A szolgáltató önálló megszakítási hibája nem jelent automatikusan hívói megszakítást.
- A hivatalos SDK szinkron natív hívásai nem fogadnak megszakítási tokent. Az adapter előttük/utánuk ellenőriz, megvárja a felszabadítást, és megszakítás után nem adja ki a titkot; a már futó hálózati művelet azonnali leállítása nem garantált.
- Minden nyers lekérés külön SDK-klienst használ; cache-találat nem inicializál SDK-klienst. Nincs fájlba mentett SDK-munkamenet. A publikáláskor a natív SDK-fájlokat meg kell őrizni; a célplatform kompatibilitása külön ellenőrizendő.
- Az alapértelmezett végpont a Bitwarden amerikai felhője. EU/self-hosted végpont konfigurálása nem támogatott.
