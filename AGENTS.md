# AGENTS.md – agent-gate fejlesztési útmutató

## Mi ez

Az **agent-gate** a köztes réteg az otthoni AI-agentek (elsősorban Mikaela) és az éles rendszerek között. Az agentek nem kapnak kulcsot: a gate MCP-n keresztül szűk, szándék-szintű műveleteket kínál, ezeket ellenőrzi, szükség esetén jóváhagyást kér, végrehajtja és naplózza.

## Architektúra

- Egyetlen ASP.NET Core Minimal API alkalmazás (.NET 10, C#), a hivatalos C# MCP SDK-val, HTTP transporttal.
- Mellette egy tesztprojekt (xUnit).
- Rétegezett „enterprise” felépítés helyett egyszerű mappák: `Tools/` (MCP eszközök), `Policy/` (kockázati szintek, jogosultságok), `Approval/` (jóváhagyás), `Audit/` (napló), `Secrets/` (Bitwarden), `Integrations/` (Home Assistant, Proxmox, MikroTik, Cloudflare kliensek). A lista bővülhet: egy új rendszer saját almappát, saját kockázati szinteket és saját teszteket kap, és mindig külön issue-ban kerül be.
- Minimális külső függőség. Minden új NuGet csomagot a PR-ben indokolni kell.
- A gate a gate VM-en fut (VLAN 41), systemd szolgáltatásként. A `main`-re történő merge után GitHub Actions self-hosted runner telepíti. A workflow-t csak Szabolcs módosíthatja.

## Biztonsági szabályok (nem alkuképesek)

1. **Titok soha** nem kerül a repóba, a naplóba, hibaüzenetbe vagy MCP-válaszba. A titkokat futásidőben a Bitwarden Secrets Managerből kéri le a `gate-sa` gépi fiók. Az egyetlen helyben tárolt titok ennek a fióknak a tokenje (root-only fájl vagy systemd credential).
2. Minden MCP-eszköz **szándék-szintű** (például `ha_create_automation`). Általános, „hívj meg bármit” eszköz nincs.
3. Minden eszköznek **kötelező kockázati szintje** van: Olvasás, Vezérlés, Érzékeny vezérlés, Konfiguráció, Rendszer vagy Tiltott. Szint nélküli eszközt egy teszt elbuktat.
4. **Jóváhagyást a gate maga kér** (Home Assistant push, Admin szoba), soha nem a hívó agent. A jóváhagyás egyedi, egyszer használható, lejár, és pontosan ahhoz a művelethez kötött, amit megmutatott.
5. Minden hívás **auditnaplóba** kerül: ki, mit, mikor, milyen paraméterekkel (titkok nélkül), és mi lett az eredmény.
6. **Bemenet-ellenőrzés mindenhol, engedélyezőlistával** (entitás-domainek, fájlútvonalak, zónák), soha nem tiltólistával.
7. A naplókból (beleértve a Home Assistant naplóit) a tokeneket és jelszavakat kiszűrjük, mielőtt bárkinek visszaadjuk.
8. **Tiltott műveletek:** a `secrets.yaml` olvasása vagy írása; a host leállítása; tűzfal, felhasználó vagy jogosultság módosítása a Proxmoxon; a management szabályok módosítása a MikroTiken; meglévő DNS rekord törlése.
9. **Vészleállítás:** egyetlen kapcsolóval minden írási művelet azonnal letiltható.

## Kockázati szintek (Home Assistant)

| Szint | Példák | Jóváhagyás |
| --- | --- | --- |
| Olvasás | eszközök, entitások, területek, zónák, naplók, irányítópultok | nem kell |
| Vezérlés | lámpa, redőny, klíma, jelenet aktiválása | nem kell |
| Érzékeny vezérlés | zár, riasztó, garázskapu | kell |
| Konfiguráció | automatizáció, szkript, jelenet, blueprint, terület, zóna, irányítópult | kell, diffel |
| Rendszer | frissítés, kiegészítők, újraindítás, mentés | kell |
| Tiltott | `secrets.yaml`, leállítás | soha |

A „diffel” azt jelenti, hogy a jóváhagyási kérés megmutatja a változást: mi volt eddig a beállítás, és mi lesz utána (a hozzáadott és törölt sorokkal). Így pontosan azt hagyod jóvá, ami történni fog. Konfiguráció módosítása előtt a gate automatikusan mentést készít. Fájlt csak engedélyezett könyvtárlistán belül szerkeszthet.

## Kódolási szabályok

- **Nyelv:** megjegyzés, commit, issue, PR és felhasználónak szóló üzenet magyarul. Az azonosítók angolul, ékezet nélkül.
- Nullable reference types bekapcsolva, a figyelmeztetések hibának számítanak.
- Végig aszinkron kód, a `CancellationToken` mindenhol továbbadva.
- Kis, egy célú osztályok, felesleges absztrakció nélkül.
- Nincs „ideiglenes” biztonsági kivétel vagy kikommentelt ellenőrzés.

## Tesztelés

- Minden új eszközhöz egységteszt: jogosultság, kockázati szint, bemenet-ellenőrzés, sikeres és hibás eset.
- Az integrációk mockolva vannak. Teszt soha nem ér el éles rendszert.
- PR előtt a `dotnet build` és a `dotnet test` hiba és figyelmeztetés nélkül fusson le.

## PR-formátum

- **Cím:** `#<issue száma> <rövid leírás>`
- **Leírás:**
  - Mi változott
  - Miért
  - Hogyan teszteltem
  - Kockázati szint
  - Érint-e jogosultságot vagy titkot (igen/nem, és ha igen, mit)
  - Új függőség (ha van, miért)

## Commit üzenetek

Magyarul, felszólító módban, röviden. Például: „Home Assistant olvasási eszköz hozzáadása”.
