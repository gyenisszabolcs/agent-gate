# Konfigurációvezérelt vészleállítás (#12)

**A modul még nincs az éles védelmi vagy végrehajtási láncba kötve.** Nem regisztrál szolgáltatást, nem változtatja a `Program.cs`-t vagy az MCP-eszközöket. Önmagában nem védi az éles műveletvégrehajtást. Nincs telepítés, éles rendszerelérés vagy éles kapcsolóállítás.

## Forrás és szerződés

A választott minimális forrás az `IOptionsMonitor<KillSwitchOptions>` alapértelmezett, név nélküli options-példánya. Külön fájlolvasó nincs. A későbbi integráció a `KillSwitch` konfigurációs szakaszt kötheti hozzá; az érték kulcsa `KillSwitch:Enabled`. Ez szerződés és tesztelt példa, **nem meglévő alkalmazásbekötés**:

```csharp
services.AddOptions<KillSwitchOptions>()
    .Bind(configuration.GetSection("KillSwitch"));
var policy = new KillSwitchPolicy(optionsMonitor);
var allowed = policy.IsAllowed(riskLevel);
```

- `Enabled = true`: kizárólag `RiskLevel.Read` engedett.
- `Enabled = false`: `Read`, `Control`, `SensitiveControl`, `Configuration`, `System` ezen a szűrőn átengedhető. Ez nem általános jogosultság vagy jóváhagyás.
- Hiányzó szakasz/érték (`Enabled = null`), hibás logikai érték miatti kötési hiba, validálási vagy olvasási kivétel: kizárólag `Read` engedett.
- `Forbidden`, nem definiált enumérték és a később esetleg hozzáadott szint mindig tiltott, amíg nincs kifejezetten engedélyezve a szűrőben.

Az `IsAllowed` **minden híváskor egyszer** olvassa a `CurrentValue` értékét, az olvasási szint vizsgálatakor is. Nem tárol korábbi engedélyező állapotot, nem használ `IOptions<T>` pillanatképet és nem iratkozik fel saját változáskezelővel. Hibánál nem használ utolsó ismert jó értéket. A forráskivételt szándékosan szélesen kezeli kizárólag az állapotolvasás körül; a hiba részleteit nem naplózza és nem adja vissza.

A monitor szolgáltatója új állapotképet és működő újratöltési értesítést köteles adni. A monitorba már bekerült változás a következő ellenőrzésre érvényes, alkalmazás-újraindítás nélkül. A modul nem garantál fájlrendszeri eseménykésleltetést, nem von vissza már kiadott döntést és nem szakít meg futó műveletet. A későbbi végrehajtási láncnak a művelet előtt kell újra ellenőriznie.

**Forráshatár:** ha egy jövőbeli fájl-/konfigurációszolgáltató hibás fájlnál csendben megtartja a korábbi `false` értéket, azt a monitor fogyasztója nem tudja felismerni. Ilyen szolgáltató közvetlen használata nem felel meg a szerződésnek: forráshibánál friss, zárt állapotot vagy a `CurrentValue` olvasásakor kivételt kell biztosítania. Valós fájlos bekötés és hibás fájlok kezelése külön integrációs feladat, nem ennek a modulnak a része.

## Tesztelés és függőségek

A saját `KillSwitchTests.cs` tesztek mind a hat szintet vizsgálják be- és kikapcsolva, valamint negatív, tartományon kívüli és maximális enumértékeket. Hiányzó beállítás, hiányzó konfigurációs szakasz, hibás kötés, forráskivétel és állapotváltás is szerepel. Tesztmonitor és valódi options monitor memóriabeli konfigurációval fut: nincs éles fájl, hálózati integráció vagy éles szolgáltatás.

A változáskezelési teszt a `false → true → false → hibás → hiányzó → false` sorozatot ellenőrzi ugyanazzal a policy-példánnyal. Hibás kötéskor az újratöltés értesítése is kivételt jelezhet; ez nem jelent engedélyező visszaesést.

Nincs új NuGet-függőség vagy projektfájl-módosítás: az options/configuration/DI a már elérhető ASP.NET Core megosztott keretrendszerből származik.

Ellenőrző parancsok a megoldás gyökerében:

```sh
dotnet build AgentGate.sln --no-restore
dotnet test AgentGate.sln --no-build --no-restore
dotnet test AgentGate.sln --no-build --no-restore --filter 'FullyQualifiedName!~KillSwitchTests'
dotnet format AgentGate.sln --no-restore --verify-no-changes
```

A tesztfuttató meglévő `VSTEST_ARTIFACTS_DIRECTORY` fallback diagnosztikája nem a modul hibája; csomagfrissítés nem része ennek a változásnak.
