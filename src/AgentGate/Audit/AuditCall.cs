using AgentGate.Policy;

namespace AgentGate.Audit;

/// <summary>Előzetesen titokmentesített, tartós hívásállapot; a régi auditbejegyzésektől elkülönítve.</summary>
public sealed record AuditCall(
    Guid CallId,
    DateTimeOffset TimestampUtc,
    string Caller,
    string Tool,
    RiskLevel RiskLevel,
    string SanitizedParameters,
    string State = "Started",
    double? DurationMilliseconds = null,
    string? SanitizedError = null);

public interface IAuditStore
{
    Task BeginAsync(AuditCall call, CancellationToken cancellationToken);
    Task CompleteAsync(Guid callId, string state, double durationMilliseconds, string? sanitizedError, CancellationToken cancellationToken);
    Task CompletePendingAsync(Guid callId, string state, string? sanitizedError, CancellationToken cancellationToken);
}

public sealed class AuditOptions
{
    public string DatabasePath { get; set; } = "/var/lib/agentgate/audit.db";
    public string Caller { get; set; } = "agent";
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolAuditParametersAttribute(params string[] names) : Attribute
{
    public IReadOnlyList<string> Names { get; } = Array.AsReadOnly(names.ToArray());
}
