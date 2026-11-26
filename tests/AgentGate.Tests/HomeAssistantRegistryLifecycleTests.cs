using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Audit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("changed")]
    [InlineData("rejected")]
    [InlineData("expired")]
    [InlineData("audit-failure")]
    [InlineData("oversized")]
    [InlineData("provider-failure")]
    public async Task RegistryLifecycleThroughRealMcpPushAndDurableAudit(string scenario)
    {
        using var registry = new HomeAssistantRegistryTests.Fixture();
        await using var app = new ChannelApp { RegistryFixture = registry };
        if (scenario == "audit-failure") app.AuditOverride = new RegistryFailPreparationStore(new SqliteAuditStore(app.AuditPath));
        if (scenario == "oversized") registry.Name = new string('Ő', 81);
        if (scenario == "provider-failure") registry.Secrets.Read = _ => throw new InvalidOperationException("Nem elérhető a mock titokszolgáltató.");
        using var client = app.CreateClient();
        await WaitAsync(() => Assert.Single(app.Services.GetServices<IApprovalChannel>()).IsAvailable);
        var durableBackupSeen = false;
        registry.BeforeWrite = () =>
        {
            using var db = new SqliteConnection($"Data Source={app.AuditPath};Pooling=False");
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT SanitizedParameters FROM AuditCalls WHERE Tool = 'ha_update_registry' AND State = 'Succeeded'";
            using var rows = query.ExecuteReader();
            while (rows.Read())
            {
                using var payload = JsonDocument.Parse(rows.GetString(0));
                if (!payload.RootElement.TryGetProperty("phase", out var phase) || phase.GetString() != "prepared") continue;
                var saved = payload.RootElement.GetProperty("parameters");
                using var before = JsonDocument.Parse(saved.GetProperty("before").GetString()!);
                using var after = JsonDocument.Parse(saved.GetProperty("after").GetString()!);
                Assert.Equal("Régi", before.RootElement.GetProperty("name").GetString());
                Assert.Equal("Új", after.RootElement.GetProperty("name").GetString());
                durableBackupSeen = true;
            }
            Assert.True(durableBackupSeen, "Nem volt tartós előállapotmentés a HA-írás előtt.");
        };
        using var pending = await CallMcpAsync(app, "ha_update_registry", new { entity_id = "sensor.room", patch = new { name = "Új" } });
        Assert.Equal(0, registry.Writes);
        var result = pending.RootElement.GetProperty("result");
        if (scenario is "audit-failure" or "oversized" or "provider-failure")
        {
            Assert.True(result.GetProperty("isError").GetBoolean());
            Assert.Equal(0, app.Http.Count);
            return;
        }
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(text[(text.LastIndexOf(' ') + 1)..], out var id));
        using var push = JsonDocument.Parse(app.Http.Body!);
        var message = push.RootElement.GetProperty("message").GetString()!;
        Assert.Equal("Jóváhagyás: név módosítása", push.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("sensor.room", message);
        Assert.Equal(2, message.Split('\n').Length);
        Assert.Contains("Név: Régi → Új", message);
        Assert.Contains("Régi", message);
        Assert.Contains("Új", message);
        var (approve, reject) = Codes(app.Http);
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        if (scenario == "changed") registry.Name = "Más";
        if (scenario == "expired")
        {
            app.Clock.Now = app.Clock.Now.AddHours(1);
        }
        app.Socket.Incoming.Writer.TryWrite(Event(scenario == "rejected" ? reject : approve));
        var expected = scenario switch
        {
            "changed" => ApprovalState.Failed,
            "rejected" => ApprovalState.Rejected,
            "expired" => ApprovalState.Expired,
            _ => ApprovalState.Executed
        };
        await WaitAsync(() => gate.GetStatus(id)?.State == expected && app.Http.Count == 2);
        Assert.Equal(scenario == "success" ? 1 : 0, registry.Writes);
        Assert.Equal(scenario == "success", durableBackupSeen);
        // Egy következő elutasítás sorompóként bizonyítja, hogy az újrajátszott kód feldolgozódott.
        app.Socket.Incoming.Writer.TryWrite(Event(approve));
        using var next = await CallMcpAsync(app, "ha_update_registry", new { entity_id = "sensor.room", patch = new { name = "Következő" } });
        var nextText = next.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(nextText[(nextText.LastIndexOf(' ') + 1)..], out var nextId));
        var (_, nextReject) = Codes(app.Http);
        app.Socket.Incoming.Writer.TryWrite(Event(nextReject));
        await WaitAsync(() => gate.GetStatus(nextId)?.State == ApprovalState.Rejected);
        Assert.Equal(scenario == "success" ? 1 : 0, registry.Writes);
    }

    private sealed class RegistryFailPreparationStore(IAuditStore inner) : IAuditStore
    {
        public Task BeginAsync(AuditCall call, CancellationToken cancellationToken) =>
            call.SanitizedParameters.Contains("\"phase\":\"prepared\"", StringComparison.Ordinal)
                ? throw new InvalidOperationException("Nem írható az előállapotmentés.") : inner.BeginAsync(call, cancellationToken);
        public Task CompleteAsync(Guid callId, string state, double durationMilliseconds, string? sanitizedError, CancellationToken cancellationToken) =>
            inner.CompleteAsync(callId, state, durationMilliseconds, sanitizedError, cancellationToken);
        public Task CompletePendingAsync(Guid callId, string state, string? sanitizedError, CancellationToken cancellationToken) =>
            inner.CompletePendingAsync(callId, state, sanitizedError, cancellationToken);
    }
}
