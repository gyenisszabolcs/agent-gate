namespace AgentGate.Policy;

/// <summary>
/// A vészleállítás állapota; a hiányzó érték nem oldja fel a védelmet.
/// </summary>
public sealed class KillSwitchOptions
{
    /// <summary>
    /// Kizárólag a kifejezett false engedi át a nem tiltott írási kockázati szinteket.
    /// </summary>
    public bool? Enabled { get; set; }
}
