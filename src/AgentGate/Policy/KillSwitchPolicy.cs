using Microsoft.Extensions.Options;

namespace AgentGate.Policy;

/// <summary>
/// Önálló vészleállítási szűrő; még nincs az éles végrehajtási láncba kötve.
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
        return level switch
        {
            RiskLevel.Read => true,
            RiskLevel.Control or RiskLevel.SensitiveControl or RiskLevel.Configuration or RiskLevel.System => !enabled,
            _ => false
        };
    }
}
