using AgentGate.Policy;
using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace AgentGate.Tools;

[McpServerToolType]
public sealed class GateTools
{
    [ToolRisk(RiskLevel.Read)]
    [McpServerTool(Name = "gate_ping", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Elérhetőségi próba: pong és a szerver aktuális UTC ideje.")]
    public static ValueTask<PingResult> Ping(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PingResult("pong", DateTimeOffset.UtcNow));
    }

    public sealed record PingResult(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("serverTime")] DateTimeOffset ServerTime);
}
