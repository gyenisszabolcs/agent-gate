# Fájlos vészleállítás

## Bekötés és döntés

A `Program.cs` regisztrálja a friss, megszakítható fájlforrást (`IKillSwitchStateSource` / `FileKillSwitchStateSource`). A közös `McpAuditFilter` sorrendje: tartós `BeginAsync` → friss kapcsolóolvasás → a #16-ból közösített `KillSwitchPolicy.IsAllowed(level, enabled)` döntés → csak engedély esetén eszközvégrehajtás → audit lezárása. A kezdő audit hibája esetén sem kapcsolóolvasás, sem eszközvégrehajtás nem történik.

- `Enabled = true`: kizárólag `Read` engedett.
- `Enabled = false`: `Read`, `Control`, `SensitiveControl`, `Configuration`, `System` ezen a szűrőn átengedhető. Ez nem jogosultság vagy jóváhagyás; a további ellenőrzéseket nem helyettesíti.
- `Forbidden` és nem definiált enumérték mindig tiltott.
- Hiányzó, olvashatatlan, hibás állapot: Read-only; a gate ettől elindulhat.
- Policy-tiltáskor nincs eszközvégrehajtás; `Failed` audit és `IsError=true` válasz készül ugyanazzal a konstans indokkal: `A vészleállító policy letiltotta az eszköz végrehajtását.`

Az előző monitoradapter (`new KillSwitchPolicy(IOptionsMonitor<KillSwitchOptions>)`, `IsAllowed(level)`) megmarad az önálló modul fogyasztóinak, ugyanazzal a közös döntési függvénnyel. Az éles auditkapu **nem** a konfigurációfigyelőre vagy a korábbi `KillSwitch:Enabled` kulcsra támaszkodik: minden hívás friss fájlolvasást végez. Nincs utolsó jó `false`, watcher-cache vagy újratöltési késleltetés miatti nyitva maradás.

## Fájl és konfiguráció

Alapértelmezett éles útvonal: `/etc/agentgate/kill-switch.json`. UTF-8 JSON, opcionális UTF-8 BOM-mal, pontosan egy `Enabled` nevű logikai mezővel:

```json
{"Enabled": true}
```

Kikapcsoláshoz:

```json
{"Enabled": false}
```

Csak a kifejezett, egyetlen `false` oldja fel az írási korlátot. Hiányzó, null, szöveges vagy numerikus érték, duplikált mező (azonos érték esetén is), ismeretlen mező, eltérő kis-/nagybetű, hibás JSON, más kódolás vagy I/O-hiba zár. Nem vezet engedélyező visszaeséshez az sem, ha az előző hívás még `false` értéket olvasott.

A szerveroldali `KillSwitch:Path` / `KillSwitch__Path` útvonal-felülírás helyi teszthosthoz használható. Ez nem MCP-argumentum; a hívó nem választhat állapotfájlt. A gate nem hozza létre és nem írja a kapcsolót vagy annak könyvtárát. HA-konfiguráció, HA-kliens-regisztráció és Bitwarden-token/-provider nem előfeltétel.

## Kézi kezelés a hoston

Ezeket az üzemeltető végzi, nem az agent és nem a szolgáltatás:

```sh
sudo install -d -o root -g root -m 0755 /etc/agentgate
sudoedit /etc/agentgate/kill-switch.json
sudo chown root:root /etc/agentgate/kill-switch.json
sudo chmod 0644 /etc/agentgate/kill-switch.json
```

A `sudoedit` szerkesztőjében a fenti `true` vagy `false` tartalmat kell menteni. A fájl root tulajdonú és a gate számára csak olvasható; a könyvtár a gate számára nem írható. A szolgáltatás `ProtectSystem=strict` védelmét nem kell lazítani, új írható útvonal nem kell. A `/var/lib/agentgate` továbbra is az audit alkalmazásadat-területe, **nem** a kapcsoló írható állapottára.

A következő auditkapu-ellenőrzés már az új tartalmat használja, restart nélkül. Automatizált konfigurációkezelésnél azonos könyvtáron belüli atomikus fájlcsere szükséges. Átmenetileg hiányzó vagy részlegesen kiírt/hibás fájl is Read-only állapotot eredményez.

## Korlátok

A döntés az ellenőrzéskor olvasott állapotra szól; nem visszamenőleges tranzakció és nem tesz atomikussá egy későbbi műveletet a fájlcserével. Már elindult eszközt nem görget vissza és nem állít le kényszerrel. A kliens megszakítási tokenje továbbmegy a fájl-I/O-ba és az eszközhöz; az audit lezárásának független, korlátos tokenje változatlan.

A fájlútvonal nem jogosultsági sandbox vagy szimbolikuslink-védelem. A root tulajdon, a szülőkönyvtár védelme és a megbízható szerverkonfiguráció üzemeltetési követelmény. A fájl nem titok. Nincs új NuGet-függőség.
