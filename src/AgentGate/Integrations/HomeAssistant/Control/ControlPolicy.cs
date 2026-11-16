using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentGate.Policy;
using AgentGate.Tools;
using Microsoft.Extensions.Options;

namespace AgentGate.Integrations.HomeAssistant.Control;

public sealed record ControlRequest(string Domain, string Service, string EntityId, JsonElement Data);

/// <summary>A konfiguráció kizárólag szűkítheti a kódbeli műveleti felső korlátot.</summary>
public sealed class ControlPolicy
{
    public const string ConfigurationError = "A Home Assistant vezérlés konfigurációja érvénytelen.";
    private sealed record DomainPolicy(bool All, FrozenSet<string> Entities, FrozenSet<string> Control, FrozenSet<string> Sensitive);
    private readonly FrozenDictionary<string, DomainPolicy> domains;
    private readonly FrozenSet<string> denied;
    private readonly FrozenSet<string> hvac;
    public bool Enabled { get; }
    public int PerEntityPerMinute { get; }
    public int GlobalPerMinute { get; }

    private ControlPolicy(bool enabled, Dictionary<string, DomainPolicy> domains, string[] denied, string[] hvac, int perEntity, int global)
    {
        Enabled = enabled;
        this.domains = domains.ToFrozenDictionary(StringComparer.Ordinal);
        this.denied = denied.ToFrozenSet(StringComparer.Ordinal);
        this.hvac = hvac.ToFrozenSet(StringComparer.Ordinal);
        PerEntityPerMinute = perEntity;
        GlobalPerMinute = global;
    }

    public static string[] MaximumServices(string domain, RiskLevel risk) => (domain, risk) switch
    {
        ("light" or "switch" or "input_boolean", RiskLevel.Control) => ["turn_on", "turn_off", "toggle"],
        ("cover", RiskLevel.Control) => ["open_cover", "close_cover", "stop_cover", "set_cover_position"],
        ("climate", RiskLevel.Control) => ["set_temperature", "set_hvac_mode", "turn_on", "turn_off"],
        ("scene", RiskLevel.Control) => ["turn_on"],
        ("lock", RiskLevel.SensitiveControl) => ["lock", "unlock"],
        ("cover", RiskLevel.SensitiveControl) => ["open_cover", "close_cover", "stop_cover"],
        _ => []
    };

    private static OptionsValidationException InvalidConfiguration() => new(Options.DefaultName, typeof(ControlPolicy), [ConfigurationError]);
    private static void Keys(IConfigurationSection section, params string[] allowed)
    {
        if (section.Value is not null || section.GetChildren().Any(c => !allowed.Contains(c.Key, StringComparer.Ordinal))) throw InvalidConfiguration();
    }
    private static string[] List(IConfigurationSection section)
    {
        var children = section.GetChildren().ToArray();
        // A JSON konfigurációprovider az üres tömböt üres stringként őrzi meg.
        if (section.Value is not null && (section.Value.Length != 0 || children.Length != 0)) throw InvalidConfiguration();
        if (children.Where((c, i) => c.Key != i.ToString(CultureInfo.InvariantCulture) || c.Value is null || c.GetChildren().Any()).Any()) throw InvalidConfiguration();
        var values = children.Select(c => c.Value!).ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw InvalidConfiguration();
        return values;
    }
    private static int Limit(IConfigurationSection section, int fallback)
    {
        if (!section.Exists() && section.Value is null && !section.GetChildren().Any()) return fallback;
        if (section.GetChildren().Any() || !int.TryParse(section.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0) throw InvalidConfiguration();
        return value;
    }
    public static ControlPolicy FromConfiguration(IConfiguration configuration)
    {
        var root = configuration.GetChildren().SingleOrDefault(c => c.Key.Equals("HomeAssistantControl", StringComparison.OrdinalIgnoreCase));
        if (root is null) return new(false, new(), [], [], 6, 60);
        Keys(root, "Domains", "EntityDenylist", "HvacModeAllowlist", "RateLimit");
        if (!root.GetChildren().Any()) throw InvalidConfiguration();
        var domainSection = root.GetSection("Domains");
        if (domainSection.Value is not null) throw InvalidConfiguration();
        var parsed = new Dictionary<string, DomainPolicy>(StringComparer.Ordinal);
        foreach (var domain in domainSection.GetChildren())
        {
            if (MaximumServices(domain.Key, RiskLevel.Control).Length + MaximumServices(domain.Key, RiskLevel.SensitiveControl).Length == 0) throw InvalidConfiguration();
            Keys(domain, "AllowAllEntities", "EntityAllowlist", "ControlServices", "SensitiveControlServices");
            var allSection = domain.GetSection("AllowAllEntities");
            var hasAll = domain.GetChildren().Any(c => c.Key == "AllowAllEntities");
            var all = false;
            if (hasAll && (allSection.GetChildren().Any() || !bool.TryParse(allSection.Value, out all))) throw InvalidConfiguration();
            var entities = List(domain.GetSection("EntityAllowlist"));
            var control = List(domain.GetSection("ControlServices"));
            var sensitive = List(domain.GetSection("SensitiveControlServices"));
            if (all && (domain.Key == "switch" || entities.Length != 0) || entities.Any(e => !CanonicalEntity(e) || !e.StartsWith(domain.Key + ".", StringComparison.Ordinal)) ||
                control.Except(MaximumServices(domain.Key, RiskLevel.Control), StringComparer.Ordinal).Any() ||
                sensitive.Except(MaximumServices(domain.Key, RiskLevel.SensitiveControl), StringComparer.Ordinal).Any()) throw InvalidConfiguration();
            parsed.Add(domain.Key, new(all, entities.ToFrozenSet(StringComparer.Ordinal), control.ToFrozenSet(StringComparer.Ordinal), sensitive.ToFrozenSet(StringComparer.Ordinal)));
        }
        var deny = List(root.GetSection("EntityDenylist"));
        var modes = List(root.GetSection("HvacModeAllowlist"));
        if (deny.Any(e => !CanonicalEntity(e) || MaximumServices(e.Split('.')[0], RiskLevel.Control).Length + MaximumServices(e.Split('.')[0], RiskLevel.SensitiveControl).Length == 0) ||
            modes.Except(new[] { "off", "heat", "cool", "heat_cool", "auto", "dry", "fan_only" }, StringComparer.Ordinal).Any()) throw InvalidConfiguration();
        var limits = root.GetSection("RateLimit");
        Keys(limits, "PerEntityPerMinute", "GlobalPerMinute");
        // A jelenlevő null skalár nem hiányzó kulcs.
        foreach (var item in limits.GetChildren()) if (item.Value is null) throw InvalidConfiguration();
        return new(true, parsed, deny, modes, Limit(limits.GetSection("PerEntityPerMinute"), 6), Limit(limits.GetSection("GlobalPerMinute"), 60));
    }

    public static bool CanonicalEntity(string? entity) => entity is { Length: <= 255 } &&
        Regex.IsMatch(entity, @"\A[a-z][a-z0-9_]*\.[a-z0-9_]+\z", RegexOptions.CultureInvariant);
    public static GateToolException Denied() => new("A Home Assistant vezérlési kérés nem engedélyezett.");
    public ControlRequest Validate(IReadOnlyDictionary<string, JsonElement> arguments, RiskLevel risk)
    {
        if (!Enabled) throw new GateToolException("A Home Assistant vezérlés nincs beállítva.");
        if (arguments.Keys.Any(k => k is not ("domain" or "service" or "entity_id" or "data"))) throw Denied();
        string Text(string name) => arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Denied();
        var domain = Text("domain");
        var service = Text("service");
        var entity = Text("entity_id");
        if (!CanonicalEntity(entity) || !entity.StartsWith(domain + ".", StringComparison.Ordinal) ||
            !domains.TryGetValue(domain, out var permission) || denied.Contains(entity) || !permission.All && !permission.Entities.Contains(entity) ||
            !(risk == RiskLevel.Control ? permission.Control : risk == RiskLevel.SensitiveControl ? permission.Sensitive : FrozenSet<string>.Empty).Contains(service) ||
            !MaximumServices(domain, risk).Contains(service, StringComparer.Ordinal)) throw Denied();
        var data = arguments.TryGetValue("data", out var input) && input.ValueKind != JsonValueKind.Null ? input : JsonSerializer.SerializeToElement(new Dictionary<string, object>());
        if (data.ValueKind != JsonValueKind.Object) throw Denied();
        var fields = data.EnumerateObject().ToArray();
        if (fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length) throw Denied();
        var values = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (domain == "light" && service is "turn_on" or "toggle")
            {
                if (field.Name == "brightness_pct" && ControlNumber.InRange(field.Value, 0, 100)) values.Add(field.Name, field.Value.Clone());
                else if (field.Name == "color_temp_kelvin" && Integer(field.Value, 2000, 6500, out var kelvin)) values.Add(field.Name, kelvin);
                else throw Denied();
            }
            else if (domain == "cover" && service == "set_cover_position" && field.Name == "position" && Integer(field.Value, 0, 100, out var position)) values.Add(field.Name, position);
            else if (domain == "climate" && service == "set_temperature" && field.Name == "temperature" && ControlNumber.InRange(field.Value, 10, 30)) values.Add(field.Name, field.Value.Clone());
            else if (domain == "climate" && service == "set_hvac_mode" && field.Name == "hvac_mode" && field.Value.ValueKind == JsonValueKind.String && hvac.Contains(field.Value.GetString()!)) values.Add(field.Name, field.Value.GetString()!);
            else throw Denied();
        }
        if (service is "set_cover_position" or "set_temperature" or "set_hvac_mode" && values.Count != 1) throw Denied();
        return new(domain, service, entity, JsonSerializer.SerializeToElement(values));
    }
    private static bool Integer(JsonElement value, int min, int max, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result) && result >= min && result <= max;
    }
}
