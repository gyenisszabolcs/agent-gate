using AgentGate.Policy;

namespace AgentGate.Audit;

/// <summary>Kizárólag előzetesen titokmentesített adatok; a tároló nem végez titokszűrést.</summary>
public sealed record SanitizedAuditEntry(
    DateTimeOffset TimestampUtc,
    string Caller,
    string Tool,
    string SanitizedParameters,
    RiskLevel RiskLevel,
    string SanitizedResult);
