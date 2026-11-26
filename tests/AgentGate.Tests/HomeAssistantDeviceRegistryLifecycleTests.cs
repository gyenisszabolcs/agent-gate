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
    [InlineData("policy-changed")]
    [InlineData("rejected")]
    [InlineData("expired")]
    [InlineData("audit-failure")]
    public async Task DeviceRegistryLifecycleThroughRealMcpPushAndDurableAudit(string scenario)
    {
        using var registry = new HomeAssistantDeviceRegistryTests.Fixture();
        await using var app = new ChannelApp { DeviceRegistryFixture = registry };
        if (scenario == "audit-failure") app.AuditOverride = new RegistryFailPreparationStore(new SqliteAuditStore(app.AuditPath));
        using var client = app.CreateClient();
        await WaitAsync(() => Assert.Single(app.Services.GetServices<IApprovalChannel>()).IsAvailable);
        var backupSeen = false;
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
                Assert.Equal("device", saved.GetProperty("device_id").GetString());
                using var before = JsonDocument.Parse(saved.GetProperty("before").GetString()!);
                using var after = JsonDocument.Parse(saved.GetProperty("after").GetString()!);
                Assert.Equal("Régi", before.RootElement.GetProperty("name_by_user").GetString());
                Assert.Equal("Új", after.RootElement.GetProperty("name_by_user").GetString());
                Assert.Equal(2, before.RootElement.GetProperty("active_entities").GetArrayLength());
                backupSeen = true;
            }
            Assert.True(backupSeen, "Az eszköz előállapota nem került tartós auditba az írás előtt.");
        };
        using var pending = await CallMcpAsync(app, "ha_update_registry", new { device_id = "device", patch = new { name_by_user = "Új" } });
        Assert.Equal(0, registry.Writes);
        var result = pending.RootElement.GetProperty("result");
        if (scenario == "audit-failure")
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
        Assert.Contains("Név: Régi → Új", message);
        Assert.DoesNotContain("device_id", message);
        var (approve, reject) = Codes(app.Http);
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        if (scenario == "changed") registry.Device["name_by_user"] = "Más";
        if (scenario == "policy-changed") registry.ActiveResponse = "[\"sensor.room\",\"sensor.denied\"]";
        if (scenario == "expired") app.Clock.Now = app.Clock.Now.AddHours(1);
        app.Socket.Incoming.Writer.TryWrite(Event(scenario == "rejected" ? reject : approve));
        var expected = scenario switch
        {
            "changed" or "policy-changed" => ApprovalState.Failed,
            "rejected" => ApprovalState.Rejected,
            "expired" => ApprovalState.Expired,
            _ => ApprovalState.Executed
        };
        await WaitAsync(() => gate.GetStatus(id)?.State == expected && app.Http.Count == 2);
        Assert.Equal(scenario == "success" ? 1 : 0, registry.Writes);
        Assert.Equal(scenario == "success", backupSeen);
        using var outcome = JsonDocument.Parse(app.Http.Body!);
        Assert.Contains("Régi → Új", outcome.RootElement.GetProperty("message").GetString());
        // Az újrajátszás után feldolgozott új elutasítás sorompó: nem egy puszta késleltetés.
        app.Socket.Incoming.Writer.TryWrite(Event(approve));
        registry.ActiveResponse = "[\"sensor.room\",\"sensor.other\"]";
        using var next = await CallMcpAsync(app, "ha_update_registry", new { device_id = "device", patch = new { name_by_user = "Következő" } });
        var nextText = next.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(nextText[(nextText.LastIndexOf(' ') + 1)..], out var nextId));
        var (_, nextReject) = Codes(app.Http);
        app.Socket.Incoming.Writer.TryWrite(Event(nextReject));
        await WaitAsync(() => gate.GetStatus(nextId)?.State == ApprovalState.Rejected);
        Assert.Equal(scenario == "success" ? 1 : 0, registry.Writes);
    }
}
