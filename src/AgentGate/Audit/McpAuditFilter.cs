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
        if (!rejected && (tool!.ProtocolTool.Name is "ha_control" or "ha_sensitive_control") && request.Params.Arguments is { } controlArguments)
        {
            // Csak e két eszköz teljesen validált, zárt sémájú skalárjai; nyers objektumot soha.
            parameters.Remove("data");
            try
            {
                var policy = services.GetRequiredService<AgentGate.Integrations.HomeAssistant.Control.ControlPolicy>();
                var normalized = policy.Validate(controlArguments.ToDictionary(p => p.Key, p => p.Value), risk);
                foreach (var field in normalized.Data.EnumerateObject())
                    parameters["data." + field.Name] = field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() : field.Value.GetRawText();
            }
            catch (AgentGate.Tools.GateToolException) { parameters["[kihagyva]"] = "[kihagyva]"; }
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
        Guid? pendingRequestId = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rejected)
            {
                result = Error("Az eszköz ismeretlen vagy auditmetaadata hiányos; a végrehajtás letiltva.");
            }
            else
            {
                var enabled = await services.GetRequiredService<IKillSwitchStateSource>().IsEnabledAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                rejected = !KillSwitchPolicy.IsAllowed(risk, enabled);
                if (rejected)
                {
                    result = Error("A vészleállító policy letiltotta az eszköz végrehajtását.");
                }
                else if ((tool!.ProtocolTool.Name is "ha_control" or "ha_sensitive_control") &&
                    request.Params.Arguments?.Keys.Any(name => name is not ("domain" or "service" or "entity_id" or "data")) == true)
                {
                    rejected = true;
                    result = Error("A Home Assistant vezérlési kérés nem engedélyezett.");
                }
                else if (risk is RiskLevel.SensitiveControl or RiskLevel.Configuration or RiskLevel.System)
                {
                    var id = await services.GetRequiredService<AgentGate.Approval.ApprovalGate>().RequestAsync(
                        call.CallId, tool!.ProtocolTool.Name, risk, allowed, request.Params.Arguments?.ToDictionary(pair => pair.Key, pair => pair.Value)
                            ?? new Dictionary<string, JsonElement>(), cancellationToken);
                    pendingRequestId = id;
                    rejected = id is null;
                    result = id is { } requestId
                        ? new CallToolResult { Content = [new TextContentBlock { Text = $"jóváhagyásra vár: {requestId}" }] }
                        : Error("A jóváhagyási kérés nem küldhető; a művelet nem indult el.");
                    if (!rejected)
                        state = "Pending";
                }
                else
                {
                    result = await next(request, cancellationToken);
                }
            }
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
        catch (AgentGate.Integrations.HomeAssistant.Control.ControlRateLimitException)
        {
            state = "Failed";
            result = Error(AgentGate.Integrations.HomeAssistant.Control.ControlRateLimitException.SafeMessage);
        }
        catch (AgentGate.Tools.GateToolException exception)
        {
            state = "Failed";
            result = Error(SecretRedactor.RedactText(exception.Message)!);
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
            // Minden hibaválasz itt már konstans vagy egyszer szűrt, megbízható üzenet; az újraszűrés megváltoztathatná.
            await store.CompleteAsync(call.CallId, state, duration,
                state is "Succeeded" or "Pending" ? null : ((TextContentBlock)result.Content[0]).Text, completion.Token);
        }
        catch (Exception)
        {
            if (pendingRequestId is { } failedId)
                await services.GetRequiredService<AgentGate.Approval.ApprovalGate>().ConfirmRequestAsync(failedId, false);
            return Error("Az audit lezárása sikertelen; a kezdőbejegyzés befejezetlen. Ez nem jelenti a már végrehajtott művelet visszagörgetését.");
        }
        if (pendingRequestId is { } committedId)
            await services.GetRequiredService<AgentGate.Approval.ApprovalGate>().ConfirmRequestAsync(committedId, state == "Pending");
        return result;
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };
}
