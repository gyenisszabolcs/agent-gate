using System.Diagnostics;
using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Audit;
using AgentGate.Secrets;

namespace AgentGate.Integrations.HomeAssistant.Registry;

public sealed partial class HomeAssistantRegistryService
{
    public async Task<string> ExecuteBatchAsync(PreparedOperation operation, ApprovalExecutionContext context, CancellationToken ct)
    {
        var members = BatchMembers(operation);
        var created = new Dictionary<int, CreatedBatchLabel>();
        var completed = 0;
        foreach (var member in members)
        {
            var callId = Guid.NewGuid();
            var started = Stopwatch.GetTimestamp();
            var auditStarted = false;
            var auditBeginAttempted = false;
            var completionAttempted = false;
            var effectStarted = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (await killSwitch.IsEnabledAsync(ct)) throw RegistryPolicy.Invalid();
                auditBeginAttempted = true;
                await BeginItemAuditAsync(member, context, callId, completed + 1, ct);
                auditStarted = true;
                effectStarted = true;
                // Az egycélú útvonal ellenőrzi a policy-t, a teljes előállapotot és az írás előtti kill/cancel állapotot.
                var result = await ExecuteBatchMemberAsync(member, created, ct);
                if (result is { } label)
                {
                    await AuditCreatedLabelAsync(member, label, context, completed + 1);
                    created.Add(completed, new(member, label));
                }
                ct.ThrowIfCancellationRequested();
                completionAttempted = true;
                using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await context.Audit.CompleteAsync(callId, "Succeeded", Stopwatch.GetElapsedTime(started).TotalMilliseconds, null, completion.Token);
                completed++;
            }
            catch (Exception error)
            {
                var reason = completionAttempted ? "A tag auditjának lezárása sikertelen; a hatás már megtörténhetett." :
                    error is RegistryPrestateChangedException ? RegistryPrestateChangedException.SafeMessage :
                    error is OperationCanceledException ? "A csomag végrehajtása megszakadt; nincs újrapróbálás." :
                    !effectStarted ? "Előzetes ellenőrzés vagy audit sikertelen; nincs további írás." :
                    "A tag sikertelen vagy bizonytalan; nincs visszagörgetés és nincs újrapróbálás.";
                var auditFailed = completionAttempted || auditBeginAttempted && !auditStarted;
                if (auditStarted && !completionAttempted)
                {
                    using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await context.Audit.CompleteAsync(callId, "Failed", Stopwatch.GetElapsedTime(started).TotalMilliseconds, reason, completion.Token); }
                    catch (Exception) { auditFailed = true; reason = "A tag és az audit lezárása sikertelen vagy bizonytalan; nincs további írás."; }
                }
                // A le nem futott tagok is külön, régi értékekkel korrelált auditsort kapnak.
                // Audit-hiba után nincs további próbálkozás; a csomag sohasem válhat sikerré.
                if (!auditFailed)
                {
                    using var finalization = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        if (!auditStarted)
                        {
                            await BeginItemAuditAsync(member, context, callId, completed + 1, finalization.Token);
                            await context.Audit.CompleteAsync(callId, "Failed", Stopwatch.GetElapsedTime(started).TotalMilliseconds, reason, finalization.Token);
                        }
                        for (var index = completed + 1; index < members.Count; index++)
                        {
                            var skippedId = Guid.NewGuid();
                            await BeginItemAuditAsync(members[index], context, skippedId, index + 1, finalization.Token);
                            await context.Audit.CompleteAsync(skippedId, "NotStarted", 0, "Korábbi tag miatt el nem indított művelet.", finalization.Token);
                        }
                    }
                    catch (Exception) { reason += " A fennmaradó tagok auditja nem igazolható."; }
                }
                throw new RegistryBatchExecutionException(new(members.Count, completed, completed + 1, reason));
            }
        }
        return new ApprovalBatchProgress(members.Count, completed, null, "Minden tag végrehajtása és tag-auditja igazolt.").Summary;
    }

    private static Task BeginItemAuditAsync(PreparedOperation member, ApprovalExecutionContext context, Guid id, int position, CancellationToken ct) =>
        context.Audit.BeginAsync(new AuditCall(id, context.Clock.GetUtcNow(), SecretRedactor.RedactText(context.Owner)!, member.Tool, member.Risk,
            JsonSerializer.Serialize(new { requestId = context.RequestId, phase = "batch_item", position, parameters = member.Parameters })), ct);
}

public sealed class RegistryBatchExecutionException(ApprovalBatchProgress progress) : Exception(progress.Summary)
{
    public ApprovalBatchProgress Progress { get; } = progress;
}
