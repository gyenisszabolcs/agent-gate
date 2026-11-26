namespace AgentGate.Integrations.HomeAssistant.Registry;

// Nem fogad külső üzenetet: a callbacken át is csak ez a rögzített hibaok jut tovább.
internal sealed class RegistryPrestateChangedException() : Exception(SafeMessage)
{
    internal const string SafeMessage = "A nyilvántartási előállapot megváltozott; új kérés és jóváhagyás szükséges.";
}
