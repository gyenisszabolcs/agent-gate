using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Audit;
using AgentGate.Policy;
using AgentGate.Secrets;
using AgentGate.Tools;

namespace AgentGate.Integrations.HomeAssistant.Registry;

public sealed partial class HomeAssistantRegistryService
{
    private async Task<PreparedOperation> PrepareCreateAsync(JsonElement value, CancellationToken ct, JsonElement? catalog = null)
    {
        policy.RequireDevice();
        var intent = LabelCreation.Intent(value);
        var before = LabelCreation.Catalog(catalog ?? await client.ReadRegistryLabelsAsync(true, ct));
        LabelCreation.RequireAbsent(before, (string)intent["name"]!);
        var display = new Dictionary<string, ApprovalDisplayChange>(StringComparer.Ordinal)
        {
            ["new_label"] = new("Nem létezik", (string)intent["name"]!)
        };
        if (intent["icon"] is string icon) display["icon"] = new("Nincs", icon);
        if (intent["color"] is string color) display["color"] = new("Nincs", color);
        var presentation = new ApprovalPresentation((string)intent["name"]!, display);
        _ = presentation.RegistryLine(extended: true);
        ct.ThrowIfCancellationRequested();
        return new(HomeAssistantRegistryOperation.ToolName, RiskLevel.Configuration, new Dictionary<string, string?>
        {
            ["create_label"] = JsonSerializer.Serialize(intent), ["before"] = "null", ["after"] = JsonSerializer.Serialize(intent),
            ["changes"] = JsonSerializer.Serialize(intent), ["label_catalog"] = before.GetRawText()
        }, requiresLosslessAudit: true, presentation: presentation);
    }

    private async Task<JsonElement> ExecuteCreateAsync(PreparedOperation operation, CancellationToken ct)
    {
        if (operation.Tool != HomeAssistantRegistryOperation.ToolName || operation.Risk != RiskLevel.Configuration || operation.Parameters.Count != 5) throw RegistryPolicy.Invalid();
        var intent = JsonSerializer.Deserialize<JsonElement>(operation.Parameters["create_label"]!);
        async Task Revalidate(CancellationToken token)
        {
            PreparedOperation current;
            try { current = await PrepareCreateAsync(intent, token); }
            catch (GateToolException) { throw new RegistryPrestateChangedException(); }
            if (current.ComputeHash("") != operation.ComputeHash("")) throw new RegistryPrestateChangedException();
            if (await killSwitch.IsEnabledAsync(token)) throw RegistryPolicy.Invalid();
            token.ThrowIfCancellationRequested();
        }
        await Revalidate(ct);
        var response = await client.CreateRegistryLabelAsync(intent, Revalidate, ct);
        return LabelCreation.Result(response, intent, JsonSerializer.Deserialize<JsonElement>(operation.Parameters["label_catalog"]!));
    }

    internal async Task<string> ExecuteStandaloneCreateAsync(PreparedOperation operation, ApprovalExecutionContext? context, CancellationToken ct)
    {
        var created = await ExecuteCreateAsync(operation, ct);
        if (context is not null)
        {
            await AuditCreatedLabelAsync(operation, created, context, null);
        }
        return "A címke létrejött. Azonosító: " + created.GetProperty("label_id").GetString();
    }

    private static async Task AuditCreatedLabelAsync(PreparedOperation operation, JsonElement created, ApprovalExecutionContext context, int? position)
    {
        var parameters = JsonSerializer.Serialize(new { requestId = context.RequestId, position, createHash = operation.ComputeHash(context.Owner), before = (object?)null, created });
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var id = Guid.NewGuid();
        await context.Audit.BeginAsync(new AuditCall(id, context.Clock.GetUtcNow(), SecretRedactor.RedactText(context.Owner)!,
            operation.Tool, operation.Risk, parameters), completion.Token);
        await context.Audit.CompleteAsync(id, "Succeeded", 0, null, completion.Token);
    }
}
