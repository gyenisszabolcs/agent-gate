using Microsoft.Extensions.Options;

namespace AgentGate.Policy;

/// <summary>
/// A vészleállítás közös döntése; a monitoradapter és a fájlos auditkapu ugyanazt a szabályt használja.
/// </summary>
public sealed class KillSwitchPolicy
{
    private readonly IOptionsMonitor<KillSwitchOptions> _options;

    public KillSwitchPolicy(IOptionsMonitor<KillSwitchOptions> options)
    {
        _options = options;
    }

    /// <summary>
    /// Minden híváskor egyszer olvassa a monitor aktuális állapotát; forráshibánál csak az olvasás engedett.
    /// Az engedély nem helyettesíti a többi jogosultsági és jóváhagyási ellenőrzést.
    /// </summary>
    public bool IsAllowed(RiskLevel level)
    {
        bool enabled;
        try
        {
            enabled = _options.CurrentValue.Enabled != false;
        }
        catch (Exception)
        {
            // A forrás olvasási, kötési vagy validálási hibája nem tarthatja nyitva a kapcsolót.
            enabled = true;
        }
        return IsAllowed(level, enabled);
    }

    /// <summary>A közös döntés friss fájlállapottal és a korábbi monitoradapterrel is azonos.</summary>
    public static bool IsAllowed(RiskLevel level, bool enabled) => level switch
    {
        RiskLevel.Read => true,
        RiskLevel.Control or RiskLevel.SensitiveControl or RiskLevel.Configuration or RiskLevel.System => !enabled,
        _ => false
    };
}
