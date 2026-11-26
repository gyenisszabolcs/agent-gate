using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Integrations.HomeAssistant.Registry;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("prepare-stale")]
    [InlineData("auth-stale")]
    public async Task RegistryInheritedAreaLifecycleBindsPreviewAndDurableAudit(string scenario)
    {
        using var registry = HomeAssistantRegistryInheritedAreaTests.Fixture();
        await using var app = new ChannelApp { RegistryFixture = registry };
        using var client = app.CreateClient();
        await WaitAsync(() => Assert.Single(app.Services.GetServices<IApprovalChannel>()).IsAvailable);
        var backupSeen = false;
        void AssertBackup()
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
                var parameters = payload.RootElement.GetProperty("parameters");
                using var before = JsonDocument.Parse(parameters.GetProperty("before").GetString()!);
                using var after = JsonDocument.Parse(parameters.GetProperty("after").GetString()!);
                Assert.Equal(JsonValueKind.Null, before.RootElement.GetProperty("area_id").ValueKind);
                Assert.Equal("bedroom", before.RootElement.GetProperty("effective_area_id").GetString());
                Assert.Equal("device", before.RootElement.GetProperty("area_source").GetString());
                Assert.Equal("living", after.RootElement.GetProperty("area_id").GetString());
                Assert.Equal("living", after.RootElement.GetProperty("effective_area_id").GetString());
                Assert.Equal("entity", after.RootElement.GetProperty("area_source").GetString());
                foreach (var snapshot in new[] { before.RootElement, after.RootElement })
                {
                    Assert.Equal("device", snapshot.GetProperty("device_id").GetString());
                    Assert.Equal("bedroom", snapshot.GetProperty("device_area_id").GetString());
                }
                backupSeen = true;
            }
            Assert.True(backupSeen);
        }
        registry.BeforeWrite = AssertBackup;
        using var pending = await CallMcpAsync(app, "ha_update_registry", new { entity_id = "sensor.room", patch = new { area_id = "living" } });
        var result = pending.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var error) && error.GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(Guid.TryParse(text[(text.LastIndexOf(' ') + 1)..], out var id));
        using var push = JsonDocument.Parse(app.Http.Body!);
        var message = push.RootElement.GetProperty("message").GetString()!;
        Assert.Equal(2, message.Split('\n').Length);
        Assert.Equal("Régi · Terület: Hálószoba (öröklött) → Nappali (saját)", message.Split('\n')[0]);
        Assert.Equal(0, registry.Writes);
        AssertBackup();
        if (scenario == "prepare-stale") registry.DeviceArea = "living";
        if (scenario == "auth-stale") HomeAssistantRegistryInheritedAreaTests.MutateDuringWriteAuthentication(registry, () => registry.DeviceArea = "living");
        var (approve, _) = Codes(app.Http);
        app.Socket.Incoming.Writer.TryWrite(Event(approve));
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        await WaitAsync(() => gate.GetStatus(id)?.State == (scenario == "success" ? ApprovalState.Executed : ApprovalState.Failed) && app.Http.Count == 2);
        Assert.Equal(scenario == "success" ? 1 : 0, registry.Writes);
        if (scenario != "success")
        {
            Assert.Equal(RegistryPrestateChangedException.SafeMessage, gate.GetStatus(id)!.Result);
            AssertRegistryAuditError(app.AuditPath, RegistryPrestateChangedException.SafeMessage);
            using var status = await CallMcpAsync(app, "gate_approval_status", new { requestId = id.ToString() });
            Assert.Contains(RegistryPrestateChangedException.SafeMessage, status.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegistryDeniedAndInvalidErrorsAreDistinctInMcpAndAudit(bool denied)
    {
        using var registry = HomeAssistantRegistryInheritedAreaTests.Fixture();
        await using var app = new ChannelApp { RegistryFixture = registry };
        using var client = app.CreateClient();
        using var patch = JsonDocument.Parse(denied ? """{"area_id":"living"}""" : """{"device_id":"private"}""");
        using var response = await CallMcpAsync(app, "ha_update_registry", new { entity_id = denied ? "sensor.denied" : "sensor.room", patch = patch.RootElement });
        var result = response.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Equal(denied ? RegistryPolicy.Denied().Message : RegistryPolicy.Invalid().Message, text);
        Assert.NotEqual(RegistryPrestateChangedException.SafeMessage, text);
        Assert.DoesNotContain("sensor.denied", text);
        Assert.DoesNotContain("private", text);
        AssertRegistryAuditError(app.AuditPath, text);
        Assert.Equal(0, registry.Socket.Calls);
        Assert.Equal(0, registry.Writes);
        Assert.Equal(0, app.Http.Count);
    }

    private static void AssertRegistryAuditError(string path, string expected)
    {
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT SanitizedError FROM AuditCalls WHERE Tool = 'ha_update_registry' AND State = 'Failed'";
        using var rows = query.ExecuteReader();
        var found = false;
        while (rows.Read())
        {
            Assert.Equal(expected, rows.GetString(0));
            found = true;
        }
        Assert.True(found);
    }

    [Fact]
    public async Task InheritedAreaPreviewCannotBeTruncatedToFitPush()
    {
        using var registry = HomeAssistantRegistryInheritedAreaTests.Fixture();
        registry.AreasCatalog = JsonSerializer.Serialize(new[]
        {
            new { area_id = "bedroom", name = new string('B', 80) },
            new { area_id = "living", name = new string('L', 80) }
        });
        await using var app = new ChannelApp { RegistryFixture = registry };
        using var client = app.CreateClient();
        using var response = await CallMcpAsync(app, "ha_update_registry", new { entity_id = "sensor.room", patch = new { area_id = "living" } });
        Assert.True(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(0, app.Http.Count);
        Assert.Equal(0, registry.Writes);
    }
}
