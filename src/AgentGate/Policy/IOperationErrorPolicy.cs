using AgentGate.Approval;

namespace AgentGate.Policy;

/// <summary>Kódban regisztrált, megbízható modul szűk hibakategóriái. A null nem kiadható hibát jelent.</summary>
public interface IOperationErrorPolicy
{
    // A null művelet közvetlen MCP-hívás; különben a jóváhagyott, változtathatatlan művelet.
    // Csak saját, rögzített kategóriaüzenetet adj vissza, ne providerüzenetet vagy inner exceptiont.
    // A közös kapu a visszaadott üzenetet egyszer titokszűri, a választ és az auditot ebből készíti.
    string? GetSafeMessage(Exception exception, PreparedOperation? operation);
}
