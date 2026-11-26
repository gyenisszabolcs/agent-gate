using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

[assembly: InternalsVisibleTo("AgentGate.Tests")]

namespace AgentGate;

internal static class GateConfiguration
{
    internal const string FilePath = "/etc/agentgate/agentgate.json";
    private static readonly HashSet<string> AllowedSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "HomeAssistant", "HomeAssistantApproval", "HomeAssistantControl", "HomeAssistantRegistry", "Audit", "Approval", "Secrets", "KillSwitch"
    };

    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "GATEMCPTOKEN", "AGENTGATEBITWARDENACCESSTOKEN", "BITWARDENACCESSTOKEN",
        "TOKEN", "ACCESSTOKEN", "REFRESHTOKEN", "APITOKEN", "BEARERTOKEN", "PASSWORD",
        "SECRET", "CLIENTSECRET", "AUTHORIZATION", "CREDENTIAL", "CREDENTIALS",
        "KEY", "APIKEY", "ACCESSKEY", "SECRETKEY", "PRIVATEKEY", "PUBLICKEY", "SIGNINGKEY",
        "ENCRYPTIONKEY", "SSHKEY", "SESSIONKEY", "CLIENTKEY", "CONSUMERKEY", "SUBSCRIPTIONKEY"
    };

    internal static void AddStartupFile(ConfigurationManager configuration, string path = FilePath)
    {
        var source = new StartupJsonSource { Path = path, Optional = true, ReloadOnChange = false };
        source.ResolveFileProvider();
        // A beépített JSON-források után, a tényleges környezeti változók elé kerül.
        var environment = configuration.Sources.Select((item, index) => (item, index))
            .First(pair => pair.item is EnvironmentVariablesConfigurationSource { Prefix: null or "" });
        configuration.Sources.Insert(environment.index, source);
    }

    private sealed class StartupJsonSource : JsonConfigurationSource
    {
        public override IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            EnsureDefaults(builder);
            return new StartupJsonProvider(this);
        }
    }

    private sealed class StartupJsonProvider(JsonConfigurationSource source) : JsonConfigurationProvider(source)
    {
        public override void Load()
        {
            try
            {
                base.Load();
                foreach (var key in Data.Keys)
                {
                    var segments = key.Split(':');
                    // Az ellenőrzés a fájl saját adatait vizsgálja, még az env-felülírás előtt.
                    // A null/üres értékű kulcs jelenléte is tiltott; a titok-GUID hivatkozások nem titkok.
                    if (!AllowedSections.Contains(segments[0]) || segments.Any(segment =>
                            SecretKeys.Contains(string.Concat(segment.Where(char.IsAsciiLetterOrDigit)))))
                        throw new InvalidOperationException();
                }
            }
            catch (Exception)
            {
                // A JSON-provider nyers hibája kulcsot és bemenetrészletet is tartalmazhat.
                // Nem őrizzük meg belső kivételként sem.
                throw new InvalidOperationException("A gate JSON konfigurációja érvénytelen.");
            }
        }
    }
}
