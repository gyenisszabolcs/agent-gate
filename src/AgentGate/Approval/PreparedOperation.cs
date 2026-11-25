using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentGate.Policy;

namespace AgentGate.Approval;

/// <summary>A handler által teljesen validált és normalizált, minden defaultot tartalmazó művelet.</summary>
public sealed class PreparedOperation
{
    public string Tool { get; }
    public RiskLevel Risk { get; }
    public FrozenDictionary<string, string?> Parameters { get; }
    public bool RequiresLosslessAudit { get; }

    public PreparedOperation(string tool, RiskLevel risk, IReadOnlyDictionary<string, string?> parameters, bool requiresLosslessAudit = false)
    {
        Tool = tool;
        Risk = risk;
        Parameters = parameters.ToFrozenDictionary(StringComparer.Ordinal);
        RequiresLosslessAudit = requiresLosslessAudit;
    }

    public string ComputeHash(string owner = "")
    {
        // V1: fix mezősorrend, ordinal kulcssorrend, JSON string/null értékek, UTF-8, SHA-256.
        var canonical = JsonSerializer.Serialize(new
        {
            version = 1,
            owner,
            tool = Tool,
            risk = (int)Risk,
            parameters = Parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value)
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>Csak kódban regisztrált, szándékszintű handler; nem tárolhat MCP-kéréscontextet.</summary>
public interface IApprovalOperationHandler
{
    string Tool { get; }
    RiskLevel Risk { get; }
    PreparedOperation Prepare(IReadOnlyDictionary<string, JsonElement> arguments) => throw new InvalidOperationException("Aszinkron előkészítés szükséges.");
    ValueTask<PreparedOperation> PrepareAsync(IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Prepare(arguments));
    }
    Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken);
}

public sealed class ApprovalTestOperation : IApprovalOperationHandler
{
    public string Tool => "gate_approval_test";
    public RiskLevel Risk => RiskLevel.SensitiveControl;
    public PreparedOperation Prepare(IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (arguments.Count != 1 || !arguments.TryGetValue("text", out var value) || value.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Érvénytelen próbaparaméter.");
        var text = value.GetString()!.Trim().Normalize(NormalizationForm.FormC);
        if (text.Length is < 1 or > 160 || text.Any(char.IsControl))
            throw new ArgumentException("Érvénytelen próbaparaméter.");
        return new PreparedOperation(Tool, Risk, new Dictionary<string, string?> { ["text"] = text });
    }
    public Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("A jóváhagyási próba végrehajtva; éles mellékhatás nem történt.");
    }
}
