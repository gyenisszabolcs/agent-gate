namespace AgentGate.Approval;

/// <summary>
/// Módosíthatatlan jóváhagyási adat: csak azonosító, művelethash és UTC lejárat, műveleti tartalom nélkül.
/// </summary>
public sealed class ApprovalRequest
{
    internal ApprovalRequest(Guid id, string operationHash, DateTimeOffset expiresAt)
    {
        Id = id;
        OperationHash = operationHash;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; }

    public string OperationHash { get; }

    public DateTimeOffset ExpiresAt { get; }
}
