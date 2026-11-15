# Titokszűrő (#8)

A `AgentGate.Secrets.SecretRedactor` önálló, még be nem kötött segédosztály.
Nem olvas fájlt, nem hív külső szolgáltatást, nem naplóz és nem kezel jogosultságot.

## API és viselkedés

- `RedactParameters(IReadOnlyDictionary<string, string?>)`: új, csak olvasható
  pillanatképet ad vissza. Nem módosítja az eredeti szótárat; megőrzi a kulcsokat,
  azok írásmódját és az ártalmatlan értékeket. A kulcsok összehasonlítása az új
  szótárban ordinális, kis/nagybetű-érzékeny: két eltérő írásmódú kulcs nem vész el.
  Érzékeny névnél a teljes érték helyére `[REDACTED]` kerül, üres/null értéknél is.
  Null szótár esetén bemeneti értéket nem tartalmazó `ArgumentNullException` keletkezik.
- `RedactText(string?)`: új szöveges eredményt ad, a bemenet változatlan marad.
  Null/üres szöveget változatlanul ad vissza. `=` vagy `:` utáni egyszeres/kettős
  idézőjeles, escape-elt vagy egyszerű idézetlen értékeket keres; a kulcsot,
  elválasztást és az idézőjeleket megőrzi. JSON-ban a beágyazott objektumok
  skaláris mezőit is felismeri. A kitakarás ismételt futtatása nem állítja vissza
  a titkot. Érzékeny nevű mezőnél az idézetlen `Bearer` és `Basic` sémát a hozzá
  tartozó következő értékkel együtt takarja ki. Ártalmatlan mező sémaértéke nem
  fogyasztja el a következő mezőt; több szomszédos mezőt külön-külön kezel.
  Érzékeny mező sémaértéke után is külön felismeri a következő hozzárendelés
  kezdetét: a `token=Bearer password="…"` és `token=Basic password='…'`
  alakban mindkét érték takart marad, a második mező neve és idézése megmarad.
  A mezőhatár vizsgálata a credential elfogyasztása előtt történik; a valódi
  credential végén álló `=`/`==` paddinget nem kezeli érték nélküli új mezőként.
  A feldolgozás előre haladó kurzorral, rekurzív újrafeldolgozás nélkül történik.
  A mezőkezdet-vizsgálat csak szomszédos név-/térközszakaszokon halad át,
  konstans számú előreolvasással; nem indít új regexkeresést a hátralévő szövegen.

A névfelismerés kultúrafüggetlen és kis/nagybetűtől független. A `token`,
`password`, `secret`, `authorization` részszavakat is keresi, így például az
`access_token`, `refreshToken`, `clientSecret`, `db.password`, `authorizationHeader`
is érzékeny. A `key` önálló vagy `_`, `-`, `.`, szóköz által határolt névelem;
a tipikus egybeírt `apiKey`, `accessKey`, `privateKey`, `publicKey`, `signingKey`,
`encryptionKey`, `sshKey`, `sessionKey`, `clientKey`, `consumerKey`, `subscriptionKey`
is felismerhető. A `monkey` és a `keyboard` nem érzékeny név.

## Korlátok és biztonsági tulajdonságok

Ez **heurisztikus szűrés, nem teljes körű titokfelismerés és nem JSON-parser**.

- Jelöletlen vagy ismeretlen nevű titokra nincs felismerési garancia. Nem dekódol
  URL-, base64- vagy Unicode-escape-elt kulcsnevet, és nem követ értékhivatkozásokat.
- Szövegben a kulcsnév ASCII betűvel vagy aláhúzással kezdődik, folytatása ASCII
  betű, szám, aláhúzás, pont vagy kötőjel. A paraméterszótárban nincs ilyen
  szintaktikai korlátozás. A kulcs és érték közötti térköz szóköz/tabulátor lehet.
- Az idézetlen érték az első whitespace, vessző, pontosvessző, `&`, kapcsos vagy
  szögletes zárójel, illetve idézőjel előtt véget ér. Szóközt vagy ilyen
  elválasztót tartalmazó titkot idézni kell; kivétel a fenti Bearer/Basic alak.
  Más autentikációs sémák több szóból álló értékei nem támogatottak.
- Érzékeny mezőhöz rendelt objektum/tömb teljes rekurzív elrejtését, hibás vagy
  lezáratlan idézést, illetve további szerializálást tartalmazó sztringek belső
  tartalmának újrafeldolgozását nem garantálja. Az érték nélküli `token=` nem titok.
- A szótár csak egy szintet és string/null értékeket kezel. Ártalmatlan nevű mező
  szöveges értékét nem vizsgálja újra; ilyen esetben külön `RedactText` szükséges.
- A részszó-illesztés túlzott kitakarást is okozhat (például `secretary`), és a
  közismert összetett `key` alakok listája nem teljes. Biztonsági határként,
  titokkezelés vagy engedélyezőlista helyettesítésére nem használható.
- `Authorization` nevű (vagy ezt részszóként tartalmazó) mezőnél a paddinges
  credential és a következő térközös/idézett érték kétértelmű alakja esetén a
  teljes szöveget kitakarja. Ez inkább ártalmatlan kontextust is elveszít, mint
  hogy egy lehetséges credential mezőnévként a kimenetben maradjon.
- Mindkét regex `NonBacktracking` motorral és egy másodperces időkorláttal fut.
  A határ nem valós idejű válaszidő-garancia. Névellenőrzési időtúllépésnél az
  adott paraméterérték, szöveges időtúllépésnél a teljes szöveg `[REDACTED]` lesz.
  A kivételt nem adja tovább és nem naplózza, mert az bemenetet is tartalmazhat.
- 65 536 UTF-16 kódegységnél hosszabb szöveget regexfuttatás nélkül teljesen
  `[REDACTED]` eredményre cserél. Ez tudatos, zárt hibázás; nagy ártalmatlan
  naplórészletet is elrejthet. A hívó kisebb, önmagukban teljes rekordokat adjon át,
  ne vágjon el titkot vagy idézett értéket tetszőleges darabolással.
- A visszaadott eredményben a nevek megmaradnak: magában a kulcsnévben sem szabad
  titkot tárolni. A bemenet az eredeti objektumban és a memóriában megmarad;
  a hívó csak a szűrt eredményt használhatja naplóhoz/MCP-válaszhoz.

## Tesztelés

A saját xUnit-tesztek minden nem üres titokértéket futásidőben generálnak.
Az ellenőrzések titkot tartalmazó expected/actual szöveg helyett logikai
feltételeket és általános magyar hibaüzenetet használnak, így a hibás teszt sem
írja ki a titkot. Nincs új NuGet-csomag: csak a .NET alapkönyvtárát használja.
A regressziós lefedés külön ellenőrzi a Bearer/Basic × egyszeres/kettős idézés
négy kombinációját, a további szomszédos mezőket és csatolt elválasztókat,
a szóköz/tabulátorral elválasztott értékeket, a credential paddingjét és az
ismételt szűrést. Hosszú érzékeny sémalánc, valamint 65 535, 65 536 és 65 537
kódegység hosszú bemenet is szerepel a célzott tesztek között.

A repó gyökeréből: `dotnet build`, `dotnet test`,
`dotnet format --verify-no-changes`.
