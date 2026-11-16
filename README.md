# agent-gate

Az AI-agentek és az otthoni rendszerek közötti, MCP HTTP felületű kapu. A gate
ellenőrzi a szándék-szintű műveleteket, szükség esetén jóváhagyást kér, és auditál.

## Konfiguráció

A gate induláskor opcionálisan beolvassa a **`/etc/agentgate/agentgate.json`** fájlt.
A prioritás alacsonytól magasig:

1. Beépített alapértékek és az alkalmazás `appsettings*.json` beállításai.
2. `/etc/agentgate/agentgate.json`.
3. Környezeti változók, például `HomeAssistant__Timeout=00:00:20`.

A meglévő .NET parancssori felülírás változatlanul megmarad. Hiányzó fájlnál az
eddigi működés marad. A fájlhoz nincs változásfigyelés vagy élő újratöltés:
módosítás után újra kell indítani a szolgáltatást. A konfigurációfájl útvonala fix;
nincs hozzá új konfigurációs kulcs, a fájl saját útvonalát sem állíthatja át.

Hibás JSON, ismeretlen felső szintű szakasz vagy ismert titokkulcs jelenléte
megszakítja az indulást: `A gate JSON konfigurációja érvénytelen.` A hiba nem
idézi vissza a fájl tartalmát, a kulcsnevet vagy a nyers belső kivételt. A fájlt
önmagában ellenőrizzük: a környezeti felülírás sem rejthet el tiltott kulcsot.
Az engedélyezett szakaszok: `HomeAssistant`, `HomeAssistantApproval`,
`HomeAssistantControl`, `Audit`, `Approval`, `Secrets`, `KillSwitch`.
A modulok meglévő érték- és szerkezetellenőrzései ezután is érvényesek.

### Titkok és jogosultságok

- **Titokérték nem kerülhet a JSON-ba.** A `GATE_MCP_TOKEN` továbbra is a
  szolgáltatás védett környezeti fájljából érkezik, nem ebből a JSON-ból.
- A többi futásidejű titkot a gate Bitwarden Secrets Managerből kéri le. A
  Bitwarden gépi token forrása élesben továbbra is a systemd credential;
  az `AGENT_GATE_BITWARDEN_ACCESS_TOKEN` JSON-kulcsként is tiltott.
- A `Token`, `AccessToken`, `Password`, `ApiKey`, `Authorization` és más ismert
  titokkulcsok beágyazva, eltérő kis-/nagybetűvel és null értékkel is tiltottak.
  Ez kulcsellenőrzés, nem tetszőleges mezőbe rejtett titkok felismerése.
- A `TokenSecretId` és `Secrets:SelfTestSecretId` **titokazonosító GUID**, nem
  titokérték; megadható a JSON-ban. A hitelesítési források részletei a
  `src/AgentGate/Secrets/README.md` dokumentumban találhatók.
- A JSON elvárt tulajdonosa és módja: **`root:agentgate 640`**. A szülőkönyvtár
  is root által kezelt legyen; a gate ne írhassa a konfigurációt vagy a
  vészleállító fájlt. Például az üzemeltető által létrehozott fájlra:

```sh
chown root:agentgate /etc/agentgate/agentgate.json
chmod 640 /etc/agentgate/agentgate.json
```

A program nem hozza létre a fájlt, nem telepít titkot, és nem állít jogosultságot.

### Teljes, titokmentes példa

A címek, entitások, felhasználó-/eszközazonosítók és GUID-ok fiktívek; üzemeltetés
előtt cserélendők. A jóváhagyási szakasz engedélyezi a HA HTTP/WebSocket csatornát.
Ha nincs szükség az integrációra vagy vezérlésre, a megfelelő teljes szakaszt
hagyd ki; üres HA-szakasz nem kapcsolja ki a validálást. A `Secrets` önteszt
indulásonként egyszer Bitwarden-lekérést indít: ha nem kéred, hagyd ki a
`SelfTestSecretId` kulcsot (a `Secrets` szakasz maradhat üres objektum).

```json
{
  "HomeAssistant": {
    "BaseUrl": "https://ha.example/",
    "AllowHttp": false,
    "TokenSecretId": "7da7370c-49d2-44da-803a-441c7b41b0a8",
    "DomainAllowlist": ["sensor", "light"],
    "Timeout": "00:00:10"
  },
  "HomeAssistantApproval": {
    "BaseUrl": "https://ha.example/",
    "AllowHttp": false,
    "TokenSecretId": "7da7370c-49d2-44da-803a-441c7b41b0a8",
    "NotifyService": "notify.mobile_app_fiktiv_admin",
    "ExpectedUserId": "fiktiv_admin_user",
    "ExpectedDeviceId": "fiktiv_admin_device",
    "TimeoutSeconds": 5,
    "MaxIdleSeconds": 60
  },
  "HomeAssistantControl": {
    "Domains": {
      "light": {
        "AllowAllEntities": false,
        "EntityAllowlist": ["light.fiktiv_nappali"],
        "ControlServices": ["turn_on", "turn_off"],
        "SensitiveControlServices": []
      }
    },
    "EntityDenylist": [],
    "HvacModeAllowlist": [],
    "RateLimit": {
      "PerEntityPerMinute": 6,
      "GlobalPerMinute": 60
    }
  },
  "Audit": {
    "DatabasePath": "/var/lib/agentgate/audit.db",
    "Caller": "mikaela"
  },
  "Approval": {
    "LifetimeMinutes": 15,
    "SendTimeoutSeconds": 5
  },
  "Secrets": {
    "SelfTestSecretId": "38484264-4838-457a-a680-cd69aeb80ad7"
  },
  "KillSwitch": {
    "Path": "/etc/agentgate/kill-switch.json"
  }
}
```

A `KillSwitch:Path` meglévő kulcs; elhagyva is
`/etc/agentgate/kill-switch.json` az alapértelmezett. Ez külön, üzemeltető által
kezelt vészleállító állapotfájl, nem a fenti indítási konfiguráció újratöltése.
Kezdetben legyen zárt (`{"Enabled":true}`); a hiányzó vagy hibás állapot is
letiltja az írási műveleteket. Ne helyezd a gate által írható `/var/lib/agentgate`
alá. A futás közbeni vészleállítás olvasási szabályai változatlanok; lásd
`src/AgentGate/Policy/KillSwitch.md`.

A bővebb 4.1a vezérlési minta:
`src/AgentGate/Integrations/HomeAssistant/Control/appsettings.example.jsonc`.
A fenti példa csak a meglévő kulcsokat használja, új konfigurációs kulcs nincs.
