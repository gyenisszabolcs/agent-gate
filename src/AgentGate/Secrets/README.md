# Bitwarden Secrets Manager titoklekérés (#10)

Önálló, még be nem kötött modul. A `Program.cs`, az MCP-eszközök és a DI-konfiguráció nem változott. Teszt és fejlesztés nem használ valódi gépi tokent és nem hív Bitwarden-szolgáltatást.

## Használat és határ

- A hívó kizárólag az `ISecretProvider.GetSecretAsync(Guid, CancellationToken)` interfészen kérjen titkot; a titokazonosító nem lehet üres.
- A `BitwardenSecretProvider` alapértelmezésben minden lekéréskor, futásidőben az `AGENT_GATE_BITWARDEN_ACCESS_TOKEN` környezeti változót olvassa. Nincs `.env`, tokenfájl, konfigurációba írt token vagy titokgyorsítótár.
- A környezetolvasó `Func<string, string?>` és az `IBitwardenSecretClient` szolgáltatói határ tesztben injektálható. Ezek megbízható belső függőségek; a környezetolvasó a `RUST_LOG` ellenőrzésénél is használatos, ezért éles használatban a tényleges folyamatkörnyezetet kell tükröznie.
- Az adapter minden kéréshez külön SDK-klienst hoz létre és felszabadítja azt, így nincs megosztott natív munkamenet és elavult tokennel működő gyorsítótár.
- Az alapértelmezett SDK-beállítás a Bitwarden amerikai felhőjét használja. EU/self-hosted végpont konfigurálása nem része ennek a modulnak.
- A visszaadott `string` bizalmas adat: a hívó nem naplózhatja és nem adhatja MCP-válaszba. A menedzselt `string` memóriájának garantált nullázását a modul nem ígéri.

## Miért hivatalos SDK, nem saját REST/kriptográfia?

Az egyetlen új közvetlen NuGet-függőség: **`Bitwarden.Secrets.Sdk` `1.0.0`**, a Bitwarden hivatalos .NET-csomagja; a hivatalos dokumentáció SDK-kat ismertet a Secrets Managerhez.[1][8]

A választás oka a titkosított protokoll helyes kezelése: az SDK a gépi tokennel történő belépéskor visszafejti a hitelesítési válasz titkosított payloadját és inicializálja a szervezeti kulcsot; a titokválasz feldolgozása a `value` mezőt `EncString` értékként értelmezi és visszafejti.[4][5] A saját kód **nem** értelmez ciphertextet plaintext titokként, nem fejleszt kriptográfiát és nem hív közvetlen REST-végpontot.

A NuGet-csomag metaadataiban rögzített SDK-forráscommit: `225e6cff4cb674241a7579f3fbd4b56075df4e86`. Az adapter ehhez a forráshoz ellenőrzött `BitwardenClient`, `Auth.LoginAccessToken(...)` és `Secrets.GetByIds(...)` API-t használja.[2][3]

A `GetByIds([secretId])` eredményéből kizárólag az egyező azonosítójú elem SDK által visszafejtett `Value` mezőjét adja vissza. Hiányzó egyezés esetén biztonságos „nem található vagy nem hozzáférhető” hiba keletkezik. Az SDK a sikertelen kérésekből általános `BitwardenException` kivételt készít; ennek szövegét nem elemezzük státuszkódért, és egy általános szolgáltatói hibát nem állítunk be bizonyított 404-ként.[3][4]

A belépésnél a `stateFile: null!` szándékos: a C# metódus paramétere nem-nullable, de az általa küldött kérés Rust `Option<PathBuf>` mezője null esetén `None`. Az SDK állapotfájlt csak `Some` esetén olvas/ír, ezért itt nincs munkamenetfájl.[2][5]

További új közvetlen csomag nincs; külön HTTP-, kriptográfiai, DI- vagy mockcsomag nem szükséges. A csomag .NET 6 managed assemblyt és natív RID-eszközöket tartalmaz; a jelenlegi .NET 10 projektben a tényleges restore/build ellenőrzött. A publikáláskor a NuGet natív fájljait meg kell őrizni; más célplatform alkalmasságát ez a fejlesztési ellenőrzés nem igazolja.

## Kötelező natív naplózásvédelem

**A folyamatot `RUST_LOG=off` környezettel kell indítani.** A beállítást futás közben nem szabad megváltoztatni; a folyamatban más kód sem inicializálhatja előbb az SDK-t eltérő naplózási beállítással.

Ennek oka nem feltételezés: a natív SDK az `env_logger::try_init()` függvényt hívja inicializáláskor, a belső hitelesítési kérelem pedig debug szinten a `client_secret` mezőt is tartalmazó objektumot naplózza.[6][7] Az adapter ezért **még az SDK létrehozása és bármilyen hálózati művelet előtt elutasít** minden olyan értéket, amely nem pontosan `off` (a hiányzó értéket és az egyedi trace/debug szűrőket is). Nem írja át a folyamat környezetét, nem irányítja át globálisan a stderr-t, és nem próbál utólag titkot szűrni a natív naplóból.

Ez üzembe helyezési előfeltétel, nem automatikusan módosított systemd-beállítás. Ebben a feladatban sem systemd-, sem alkalmazásbeli bekötés nem történt.

## Hibák és megszakítás

A `SecretProviderException.Error` biztonságos hibakódjai: `MissingAccessToken`, `NotFound`, `AuthenticationFailed`, `ProviderFailure`, `UnsafeDiagnostics`. Minden üzenet rögzített magyar szöveg. Az adapter belépési `BitwardenAuthException` kivételéből kizárólag egy rögzített hitelesítési jelzés jut a külső biztonsági határig; más nyers SDK-/környezetolvasó-kivétel sem jut tovább.[2]

Nincs belső szolgáltatói kivétel, nyers válasz vagy kiegészítő adat a kifelé dobott hibában. Megszakításkor új, rögzített magyar üzenetű `OperationCanceledException` keletkezik az eredeti hívói tokennel, szolgáltatói `InnerException` nélkül. Ha a szolgáltató önállóan dob megszakítási kivételt, miközben a hívó nem szakított meg, az általános szolgáltatói hiba, nem hamis hívói megszakítás.

**SDK-korlát:** a vizsgált hivatalos C# belépés és lekérés szinkron, nincs `CancellationToken` paraméterük.[2][3] Az aszinkron adapter `Task.Run` ütemezést és ugyanazt a hívói megszakítási tokent használja; ellenőriz a natív hívások előtt és után. A már futó natív hálózati hívást nem tudja azonnal megszakítani. A modul megvárja a hívást és az erőforrás-felszabadítást; nem hagy hátra elfelejtett háttérfeladatot, és megszakítás után nem adja ki a lekért titkot. Az azonnali hálózati megszakítás követelménye más SDK-t vagy külön izolált folyamatra vonatkozó további döntést igényelne.

## Ellenőrzés

TDD: az új működési szeletek előtt ténylegesen futott és elbukott a hozzájuk tartozó teszt, utána a célzott és teljes tesztkészlet sikeres lett. A tesztek csak futásidőben generált véletlen értéket használnak; a titkos értéket kezelő állítások hiba esetén sem írják ki a várt/tényleges titkot, szolgáltatói kivételt vagy belső kivételt.

- Új tesztek: 19 sikeres, 0 hibás, 0 kihagyott.
- Teljes készlet: 33 sikeres (14 változatlan baseline + 19 új), 0 hibás, 0 kihagyott.
- `dotnet build`: 0 figyelmeztetés, 0 hiba.
- `dotnet test`: sikeres; a már baseline-on is megjelenő `VSTEST_ARTIFACTS_DIRECTORY` fallback tájékoztatás nem új hiba.
- `dotnet format --verify-no-changes --no-restore`: sikeres.
- Nincs valódi tokenes/hálózati integrációs próba. A valódi adapter a tényleges hivatalos SDK-val lefordul; a siker/hiba/megszakítás egységtesztjei fake Bitwarden-határt használnak. A natív naplózás elutasítási tesztjei az SDK inicializálása előtt megállnak.

## Források

[1] https://www.nuget.org/packages/Bitwarden.Secrets.Sdk/1.0.0
[2] https://raw.githubusercontent.com/bitwarden/sdk-sm/225e6cff4cb674241a7579f3fbd4b56075df4e86/languages/csharp/Bitwarden.Sdk/AuthClient.cs
[3] https://raw.githubusercontent.com/bitwarden/sdk-sm/225e6cff4cb674241a7579f3fbd4b56075df4e86/languages/csharp/Bitwarden.Sdk/SecretsClient.cs
[4] https://docs.rs/crate/bitwarden-sm/1.0.0/source/src/secrets/secret_response.rs
[5] https://docs.rs/crate/bitwarden-core/1.0.0/source/src/auth/login/access_token.rs
[6] https://raw.githubusercontent.com/bitwarden/sdk-sm/225e6cff4cb674241a7579f3fbd4b56075df4e86/crates/bitwarden-c/src/c.rs
[7] https://docs.rs/crate/bitwarden-core/1.0.0/source/src/auth/api/request/access_token_request.rs
[8] https://bitwarden.com/help/secrets-manager-sdk
