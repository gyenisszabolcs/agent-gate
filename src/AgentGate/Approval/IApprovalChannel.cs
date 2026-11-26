using AgentGate.Policy;

namespace AgentGate.Approval;

/// <summary>Megbízható, szerveroldali csatorna; nincs MCP-n elérhető döntésbeadó felület.</summary>
public interface IApprovalChannel
{
    bool IsAvailable { get; }
    string Name { get; }
    Task SendRequestAsync(ApprovalNotice request, CancellationToken cancellationToken);
    IAsyncEnumerable<ApprovalDecision> ReadDecisionsAsync(CancellationToken cancellationToken);
    Task SendOutcomeAsync(ApprovalOutcome outcome, CancellationToken cancellationToken);
}

// A csatorna csak szűrt megjelenítési adatot kap, soha nem a végrehajtási paramétereket vagy hash-t.
public sealed record ApprovalNotice(Guid Id, string Caller, string Tool, RiskLevel Risk,
    string Summary, DateTimeOffset ExpiresAt, ApprovalPresentation? Presentation = null);
public sealed record ApprovalDecision(Guid Id, ApprovalChoice Choice, string Actor, DateTimeOffset DecidedAt);
public enum ApprovalChoice { Approve, Reject }
public enum ApprovalState { Pending, Executed, Rejected, Expired, Failed }
public sealed record ApprovalOutcome(Guid Id, ApprovalState State, string? Result);
