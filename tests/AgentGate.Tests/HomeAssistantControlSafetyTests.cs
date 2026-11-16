using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Audit;
using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantControlSafetyTests
{
    [Theory]
    [InlineData("ha_control", "light")]
    [InlineData("ha_sensitive_control", "lock")]
    public async Task KillSwitchAtIngressOrDurableBeginFailurePreventsAnyControlIo(string tool, string domain)
    {
        await using var f = new ControlFixture();
        await File.WriteAllTextAsync(f.App.KillSwitchPath, "{\"Enabled\":true}");
        using var killed = await f.Call(tool, domain, domain == "lock" ? "unlock" : "turn_on");
        Assert.True(ControlFixture.Failed(killed));
        Assert.Empty(f.Calls);
        Assert.Empty(f.Channel.Requests);
        await File.WriteAllTextAsync(f.App.KillSwitchPath, "{\"Enabled\":false}");
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.App.AuditPath};Pooling=False");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_control_begin BEFORE INSERT ON AuditCalls BEGIN SELECT RAISE(ABORT,'Tesztelt audithiba.'); END;";
        await command.ExecuteNonQueryAsync();
        using var blocked = await f.Call(tool, domain, domain == "lock" ? "unlock" : "turn_on");
        Assert.True(ControlFixture.Failed(blocked));
        Assert.Empty(f.Calls);
        Assert.Empty(f.Channel.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KillSwitchChangedDuringFreshCoverReadStopsPostAndSensitivePush(bool sensitive)
    {
        await using var f = new ControlFixture { Entity = "cover.room", Attributes = JsonSerializer.SerializeToElement(new { device_class = sensitive ? "garage" : "blind" }) };
        f.Respond = async (request, ct) =>
        {
            await File.WriteAllTextAsync(f.App.KillSwitchPath, "{\"Enabled\":true}", ct);
            return f.Response(request);
        };
        using var reply = await f.Call(sensitive ? "ha_sensitive_control" : "ha_control", "cover", "open_cover");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(0, f.Posts);
        Assert.Empty(f.Channel.Requests);
    }

    [Theory]
    [InlineData("post")]
    [InlineData("readback")]
    [InlineData("audit")]
    public async Task PostFailureOrReadbackOrFinalAuditNeverRetriesOrClaimsRollback(string failure)
    {
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var logs = new Audit.McpAuditTests.RecordingLogger();
        await using var f = new ControlFixture(services => services.AddLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs))) { Entity = "light.room" };
        f.Respond = (request, _) =>
        {
            if (failure == "post" || failure == "readback" && request.Method == HttpMethod.Get) throw new HttpRequestException(canary);
            return Task.FromResult(f.Response(request));
        };
        if (failure == "audit")
        {
            await new SqliteAuditStore(f.App.AuditPath).ReadAllAsync(CancellationToken.None);
            await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.App.AuditPath};Pooling=False");
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER fail_control_complete BEFORE UPDATE ON AuditCalls BEGIN SELECT RAISE(ABORT,'Tesztelt audithiba.'); END;";
            await cmd.ExecuteNonQueryAsync();
        }
        using var reply = await f.Call("ha_control", "light", "toggle");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Equal(1, f.Posts);
        var text = reply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("visszagörgetés", text, StringComparison.Ordinal);
        Assert.False(text.Contains(canary, StringComparison.Ordinal) || text.Contains(f.Secrets.Token, StringComparison.Ordinal) || logs.Contains(canary) || logs.Contains(f.Secrets.Token), "Titok a vezérlési hibában vagy naplóban.");
    }

    [Fact]
    public async Task CompleteImmutableNormalizedPayloadBindsOwnerToolRiskAndEveryParameter()
    {
        await using var f = new ControlFixture();
        var handler = f.App.Services.GetServices<IApprovalOperationHandler>().Single(h => h.Tool == "ha_sensitive_control");
        var arguments = HomeAssistantControlPolicyTests.Arguments("lock", "unlock");
        var operation = await handler.PrepareAsync(arguments, CancellationToken.None);
        var hash = operation.ComputeHash("owner");
        arguments["entity_id"] = JsonSerializer.SerializeToElement("lock.other");
        Assert.Equal(hash, operation.ComputeHash("owner"));
        Assert.Equal("lock.room", operation.Parameters["entity_id"]);
        Assert.Equal("{}", operation.Parameters["data"]);
        Assert.NotEqual(hash, operation.ComputeHash("other"));
        foreach (var field in operation.Parameters.Keys)
        {
            var changed = operation.Parameters.ToDictionary(p => p.Key, p => p.Value);
            changed[field] += "x";
            Assert.NotEqual(hash, new PreparedOperation(operation.Tool, operation.Risk, changed).ComputeHash("owner"));
        }
        Assert.NotEqual(hash, new PreparedOperation("ha_control", operation.Risk, operation.Parameters).ComputeHash("owner"));
        Assert.NotEqual(hash, new PreparedOperation(operation.Tool, RiskLevel.Control, operation.Parameters).ComputeHash("owner"));
    }
}
