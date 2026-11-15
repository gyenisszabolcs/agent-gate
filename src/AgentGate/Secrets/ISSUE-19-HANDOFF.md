# #19 átadás – systemd credential tokenforrás

## Eredmény és fájlok

A `BitwardenSecretProvider` a nem-null `CREDENTIALS_DIRECTORY` mellett kizárólag
a konfigurált credentialfájlt használja; hiba esetén sem olvassa az env-tokent.
A konstruktor új, opcionális `credentialFileName` argumentuma alapértelmezésben
`bitwarden-access-token`. Hiányzó/null gyökér mellett megmaradt az
`AGENT_GATE_BITWARDEN_ACCESS_TOKEN` fejlesztői forrás. Mindkét forráson `TrimEnd()`
történik; az eleje változatlan, az ezután üres token tiltott.

Érintett fájlok:

- `src/AgentGate/Secrets/BitwardenSecretProvider.cs`: forrásválasztás, fájlnév- és
  gyökérellenőrzés, közvetlen symlink/reparse point elutasítása, CT-s async olvasás.
- `src/AgentGate/Secrets/README.md`: konfiguráció, hibák és bizalmi szerződés.
- `src/AgentGate/Secrets/ISSUE-19-HANDOFF.md`: ez az átadás.
- `tests/AgentGate.Tests/SystemdCredentialTests.cs`: saját tempkönyvtáras,
  futásidőben generált értékű, fake SDK-határos tesztek.
- `tests/AgentGate.Tests/BitwardenSecretProviderTests.cs`: a meglévő env-reader
  fake-ek az új `CREDENTIALS_DIRECTORY` lekérdezésre nullt adnak; a korábbi
  biztonsági tesztek elvárásai megmaradtak.

`ISecretProvider`, a GUID API, a natív adapter, a `RUST_LOG=off` védelem, a rögzített
magyar kivételüzenetek és az SDK-hibahatár változatlan. Új NuGet-csomag nincs.
`Program.cs`, DI, projektfájlok, `.github`, `AGENTS.md` és más modul nem változott.
A #16 modulra nincs hivatkozás, testvérágfüggőség nincs. Nincs commit, push vagy PR.

## Tényleges TDD-futások

A viselkedési szeletek külön RED → GREEN futást kaptak:

1. Credential prioritás: RED 1/1 hibás (`Nem a credential token került
   továbbításra.`), utána GREEN; teljes készlet ekkor 200 sikeres.
2. Konfigurált fájlnév: a célzott futás RED fordítási hibával (`CS1739`, még nincs
   `credentialFileName` paraméter); az új paraméter és használata után 2/2 célzott
   sikeres. Ez API-hiányt bizonyító fordítási RED volt, nem futásidejű assertion-RED.
3. Kizárólag záró whitespace levágása: RED 2/2 hibás (env és credential),
   `TrimEnd()` után 4/4 célzott sikeres.
4. Fájlnévhatár: RED 2 hibás / 11 eset (létező abszolút cél és `../` kilépés),
   engedélyezőlista után 15/15 célzott sikeres.
5. Hibás gyökér: RED 1 hibás / 6 eset (létező relatív gyökér), ellenőrzés után
   21/21 célzott sikeres.
6. Symlinkek: RED 3 hibás / 4 eset (belső, külső és gyökérlink; a dangling már
   biztonságosan hibázott), ellenőrzés után 25/25 célzott sikeres.

A további tesztek a már meglévő biztonságos hibahatár és a fenti implementáció
regressziós ellenőrzései: hiányzó/üres/whitespace/könyvtár credential, valódi Unix
olvasási tiltás, null gyökér/env-only, előre és a gyökér olvasása közben kért
megszakítás, érzékeny generált fájlnév nyers I/O-hibájának elrejtése. Ezekhez nem
állítunk új, külön assertion-RED-et. A fájlnévtesztek közül több már létező,
olvasható tiltott célfájllal is ellenőrzi az elutasítást.

## Végső ellenőrzés

Környezet: .NET SDK `10.0.404`, Linux, nem root `bruce` (uid 1001).

- Baseline: első `dotnet test AgentGate.sln --no-restore` nem futott végig,
  `NETSDK1004` (az új worktree-ben még nincs assets fájl).
- Baseline restore után: `dotnet test AgentGate.sln` → **199 sikeres,
  0 hibás, 0 kihagyott**.
- `dotnet build AgentGate.sln` → **0 figyelmeztetés, 0 hiba**, exit 0.
- `dotnet test AgentGate.sln --logger 'trx;LogFileName=issue-19-full.trx'
  --results-directory /home/bruce/.hermes/cache/scratch/issue-19-test-results`
  → **233 sikeres, 0 hibás, 0 kihagyott**, exit 0.
- `dotnet test AgentGate.sln --no-build
  --filter FullyQualifiedName~SystemdCredentialTests
  --logger 'trx;LogFileName=issue-19-focused.trx'
  --results-directory /home/bruce/.hermes/cache/scratch/issue-19-test-results`
  → **34 új célzott eset sikeres, 0 hibás, 0 kihagyott**, exit 0.
- `dotnet format AgentGate.sln --verify-no-changes --no-restore` → exit 0.
- `git diff --check` → exit 0.

A `VSTEST_ARTIFACTS_DIRECTORY environment variable was not found, falling back
...` tájékoztatás baseline-on és a végső teszteknél is megjelent. Nem lett elrejtve,
nem lett globális ENV-beállítással vagy projekt-/csomagmódosítással megkerülve.
A TRX-fájlok a worktree-n kívüli fenti scratch könyvtárban vannak.

## Biztonsági korlátok és fennmaradó ellenőrzés

- Minden tokent/teszttitkot futásidőben generálunk; a titokkal dolgozó assertionök
  csak rögzített magyar szöveget adnak, hibánál sem írják ki az értéket vagy a nyers
  kivételt. Nincs globális környezetmódosítás. A tempkönyvtárakat `Dispose` törli;
  az olvasási tiltást a teszt `finally` blokkban visszaállítja.
- A Unix olvasási tiltás tesztje tényleges `UnauthorizedAccessException` hibát is
  ellenőrzött a saját fájlon. Root vagy Windows futtatást nem tekint igazolt
  jogosultságtesztnek; ezen a Linux/nem-root környezeten valóban lefutott.
- A megszakítás előre, illetve a gyökérolvasóból indítva determinisztikusan
  ellenőrzött. Külön, már folyamatban levő lemezműveletet időzítéssel megszakító
  teszt nincs; az async fájl-API közvetlenül megkapja a hívói CT-t.
- A root és ősei megbízható, nem támadó által módosítható systemd-környezetet
  feltételeznek. Nincs atomi no-follow/openat garancia írható root ellen;
  a részletes szerződés a README-ben van. Éles mount-/jogosultság-ellenőrzés,
  credentialtelepítés, systemd unit, valódi Bitwarden-/VM-/HA-elérés nem történt.
- A meglévő szinkron natív SDK folyamatban levő hálózati hívását továbbra sem tudja
  azonnal megszakítani a modul; ez változatlan, dokumentált SDK-korlát.
- Független biztonsági reviewer ellenőrzése még hátravan a főügynöknél. A végrehajtó
  subagent saját diff- és tesztellenőrzése nem minősül független jóváhagyásnak.
  Ebben a környezetben külön `codex`/`claude` CLI nem található.

Implementációs döntési akadály vagy új csomagigény nincs. Commit/push/PR tilos
maradt; a folytatás a független ellenőrzés, nem automatikus publikálás.
