namespace AgentGate.Integrations.HomeAssistant.Control;

/// <summary>Kizárólag a helyi kvótaelutasítás állandó, kiadható eredménye; szolgáltatói szöveget nem fogad.</summary>
internal sealed class ControlRateLimitException() : Exception(SafeMessage)
{
    internal const string SafeMessage = "A Home Assistant vezérlési gyakorisági korlát betelt.";
}
