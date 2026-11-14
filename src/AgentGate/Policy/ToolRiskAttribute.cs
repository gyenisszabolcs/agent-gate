namespace AgentGate.Policy;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ToolRiskAttribute(RiskLevel level) : Attribute
{
    public RiskLevel Level { get; } = level;
}
