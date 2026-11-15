# Bitwarden Secrets Manager titoklekérés

## Használat

A titkot az `ISecretProvider.GetSecretAsync(Guid, CancellationToken)` interfészen keresztül kell lekérni. Az azonosító nem lehet üres. A visszaadott `string` bizalmas adat: nem naplózható és nem adható MCP-válaszba. A menedzselt string memóriájának garantált nullázását a modul nem ígéri.

A `BitwardenSecretProvider` minden lekéréskor futásidőben választ tokenforrást. A `Program.cs` singleton `ISecretProvider` példánya `credentialOnly: true` beállítással működik: a gate **kizárólag systemd credentialt** fogad el, hiányzó `CREDENTIALS_DIRECTORY` mellett sem olvassa az env-tokent. A konstruktor alapértelmezett `credentialOnly: false` értéke a korábbi, külön példányosított fejlesztői használattal forráskompatibilis:

- Ha a `CREDENTIALS_DIRECTORY` nincs beállítva (null), az `AGENT_GATE_BITWARDEN_ACCESS_TOKEN` fejlesztői környezeti változót olvassa.
- Bármely nem-null `CREDENTIALS_DIRECTORY` kizárólagos credentialmódot jelent. Hibás, üres, nem létező vagy nem olvasható credential esetén nincs visszaesés az env-tokenre, és azt nem is olvassa ki.
- A credentialfájlnév a konstruktor `credentialFileName` argumentuma; alapértéke `bitwarden-access-token`.
- Mindkét forráson csak a záró whitespace kerül levágásra (`TrimEnd`); az eleje változatlan. Az ezután üres token elutasított.

```csharp
ISecretProvider provider = new BitwardenSecretProvider(
    credentialFileName: "bitwarden-access-token");
```

A környezetolvasó és az `IBitwardenSecretClient` megbízható, helyettesíthető függőség. Éles használatban a környezetolvasónak a tényleges folyamatkörnyezetet kell tükröznie. Az `ISecretProvider` GUID-alapú API-ja nem végez név szerinti keresést.

## Induláskori, opcionális önteszt

A `Program.cs` regisztrálja a `SecretSelfTestService` háttérszolgáltatást. A `Secrets:SelfTestSecretId` elhagyása teljesen kikapcsolja: nincs szolgáltatófeloldás, tokenolvasás, SDK-hívás vagy öntesztnapló. Üres, hibás vagy csupa nulla GUID rögzített `Configuration` hibát naplóz, az érték visszaírása nélkül. Érvényes, nem üres GUID esetén indulásonként egyszer olvas, a háttérben; nincs új MCP-eszköz vagy Home Assistant-regisztráció.

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
- A fájl aszinkron olvasása megkapja a hívó `CancellationToken` értékét. Nincs `.env`, titokgyorsítótár vagy automatikus credentialtelepítés.
- A systemd által biztosított gyökérnek és őseinek megbízhatónak, nem módosíthatónak kell lenniük. A metaadat-ellenőrzés és megnyitás nem atomi `openat`/`O_NOFOLLOW` védelem támadó által írható könyvtár ellen. A modul nem ellenőrzi vagy állítja át a mountot és a tulajdonost.

## Kötelező SDK-naplózásvédelem

A folyamatot **`RUST_LOG=off`** környezettel kell indítani. Ezt futás közben nem szabad megváltoztatni, és más kód sem inicializálhatja előbb az SDK-t eltérő beállítással. A natív SDK debug naplója hitelesítési adatot tartalmazhat; az adapter ezért minden nem pontosan `off` értéknél még a natív inicializálás előtt elutasít.

Az adapter nem módosítja globálisan a környezetet és nem irányítja át a stderr-t. A befogadó alkalmazás feladata a DI- és üzemeltetési beállítások biztosítása.

## Hibák és korlátok

- Minden kifelé adott hiba rögzített magyar üzenetű. Nyers szolgáltatói/fájlrendszeri kivétel, útvonal, fájltartalom, `InnerException` vagy `Data` nem jut át a határon.
- A hibakódok: `MissingAccessToken`, `NotFound`, `AuthenticationFailed`, `ProviderFailure`, `UnsafeDiagnostics`. A nem található és a gépi fióknak nem hozzáférhető titok nem különböztethető meg biztosan.
- Megszakításkor új, biztonságos `OperationCanceledException` keletkezik az eredeti hívói tokennel. A szolgáltató önálló megszakítási hibája nem jelent automatikusan hívói megszakítást.
- A hivatalos SDK szinkron natív hívásai nem fogadnak megszakítási tokent. Az adapter előttük/utánuk ellenőriz, megvárja a felszabadítást, és megszakítás után nem adja ki a titkot; a már futó hálózati művelet azonnali leállítása nem garantált.
- Minden lekérés külön SDK-klienst használ. Nincs fájlba mentett SDK-munkamenet. A publikáláskor a natív SDK-fájlokat meg kell őrizni; a célplatform kompatibilitása külön ellenőrizendő.
- Az alapértelmezett végpont a Bitwarden amerikai felhője. EU/self-hosted végpont konfigurálása nem támogatott.
