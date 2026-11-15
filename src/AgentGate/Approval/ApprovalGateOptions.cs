namespace AgentGate.Approval;

public sealed class ApprovalGateOptions
{
    public int LifetimeMinutes { get; set; } = 15;
    public int SendTimeoutSeconds { get; set; } = 5;
}
