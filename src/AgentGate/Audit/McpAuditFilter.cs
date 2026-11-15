using System.Diagnostics;
using System.Text.Json;
using AgentGate.Secrets;
using AgentGate.Policy;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentGate.Audit;

/// <summary>Minden regisztrált MCP-eszköz közös, tartós előzetes auditkapuja.</summary>
public sealed class McpAuditFilter(IServiceProvider services, IOptions<AuditOptions> options)
{
    public async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        CancellationToken cancellationToken)
    {
        var tool = request.MatchedPrimitive as McpServerTool;
        var riskMetadata = tool?.Metadata.OfType<ToolRiskAttribute>().SingleOrDefault();
        var allowlist = tool?.Metadata.OfType<ToolAuditParametersAttribute>().SingleOrDefault();
        var validRisk = riskMetadata is not null && Enum.IsDefined(riskMetadata.Level);
        var rejected = tool is null || !validRisk || allowlist is null;
        // A Forbidden itt a zárt elutasítás besorolása, nem az ismeretlen eszköz üzleti kockázata.
        var risk = validRisk ? riskMetadata!.Level : RiskLevel.Forbidden;
        var allowed = allowlist?.Names ?? [];
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!rejected && request.Params.Arguments is { } arguments)
        {
            foreach (var name in allowed)
            {
                if (arguments.TryGetValue(name, out var value))
                {
                    parameters[name] = value.ValueKind switch
                    {
                        JsonValueKind.String => SecretRedactor.RedactText(value.GetString()),
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => SecretRedactor.RedactText(value.GetRawText()),
                        JsonValueKind.Null => null,
                        _ => "[kihagyva]"
                    };
                }
            }
            // A nem engedélyezett nevek is hordozhatnak titkot: csak közös jelölő marad.
            if (arguments.Keys.Any(name => !allowed.Contains(name, StringComparer.Ordinal)))
            {
                parameters["[kihagyva]"] = "[kihagyva]";
            }
        }
        var call = new AuditCall(Guid.NewGuid(), DateTimeOffset.UtcNow, SecretRedactor.RedactText(options.Value.Caller)!,
            tool?.ProtocolTool.Name ?? "[ismeretlen-eszköz]", risk, JsonSerializer.Serialize(SecretRedactor.RedactParameters(parameters)));
        IAuditStore store;
        try
        {
            store = services.GetRequiredService<IAuditStore>();
            await store.BeginAsync(call, cancellationToken);
        }
        catch (Exception)
        {
            return Error("Az előzetes auditírás sikertelen; az eszköz nem indult el.");
        }
        var started = Stopwatch.GetTimestamp();
        CallToolResult result;
        var state = "Succeeded";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = rejected
                ? Error("Az eszköz ismeretlen vagy auditmetaadata hiányos; a végrehajtás letiltva.")
                : await next(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsError is true)
            {
                state = "Failed";
                if (!rejected)
                {
                    result = Error("Az eszköz végrehajtása sikertelen.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            state = "Cancelled";
            result = Error("Az eszközhívás megszakadt.");
        }
        catch (Exception)
        {
            // A provider tetszőleges, jelöletlen titkot is dobhat: az eredeti kivételt nem továbbítjuk.
            state = "Failed";
            result = Error("Az eszköz végrehajtása sikertelen.");
        }
        var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        // A kliens megszakítása nem törölheti a már tartós kezdőbejegyzés lezárási kísérletét.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await store.CompleteAsync(call.CallId, state, duration,
                state == "Succeeded" ? null : SecretRedactor.RedactText(((TextContentBlock)result.Content[0]).Text), completion.Token);
        }
        catch (Exception)
        {
            return Error("Az audit lezárása sikertelen; a kezdőbejegyzés befejezetlen. Ez nem jelenti a már végrehajtott művelet visszagörgetését.");
        }
        return result;
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };
}
