using System.Text.Json;
using System.Text.Json.Nodes;
using AgentGate.Approval;
using AgentGate.Approval.HomeAssistant;
using AgentGate.Integrations.HomeAssistant.Registry;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantRegistryBatchTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task CountOutsideOneToFiftyFailsBeforeReads(int count)
    {
        using var f = new BatchFixture();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Handler.PrepareAsync(f.Arguments(count), default).AsTask());
        Assert.Equal(0, f.Reads);
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task FiftyMembersRemainCompleteInHashStatusAndNotification()
    {
        using var f = new BatchFixture();
        var operation = await f.Prepare(50);
        using var members = JsonDocument.Parse(operation.Parameters["items"]!);
        Assert.Equal(50, members.RootElement.GetArrayLength());
        Assert.Equal(50, operation.Presentation!.Items!.Count);
        var notice = Notice(operation);
        var formatter = new ApprovalPushFormatter();
        var details = formatter.RegistryBatchDetails(notice, "canaryToken");
        Assert.Contains("50. Szenzor · Név: Szenzor → Új név (sensor.item49)", details);
        Assert.Equal("Jóváhagyás: 50 elem módosítása", formatter.Format(notice, "canaryToken").Title);
        Assert.DoesNotContain("sensor.item", formatter.Format(notice, "canaryToken").Message);
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("duplicate")]
    [InlineData("nested")]
    [InlineData("extra")]
    [InlineData("duplicate_json")]
    [InlineData("selector")]
    public async Task AmbiguousBatchInputFailsBeforeReads(string defect)
    {
        using var f = new BatchFixture();
        var arguments = f.Arguments(2);
        switch (defect)
        {
            case "mixed": arguments["entity_id"] = JsonSerializer.SerializeToElement("sensor.item0"); break;
            case "duplicate": arguments = Batch(f.Member(0), f.Member(0)); break;
            case "nested": arguments = Batch(f.Arguments(1)); break;
            case "extra": arguments = Batch(new Dictionary<string, JsonElement> { ["bogus"] = JsonSerializer.SerializeToElement(true) }); break;
            case "duplicate_json": arguments["items"] = JsonDocument.Parse("[{\"entity_id\":\"sensor.item0\",\"entity_id\":\"sensor.item1\",\"patch\":{\"name\":\"Új név\"}}]").RootElement.Clone(); break;
            case "selector": arguments["items"] = JsonSerializer.SerializeToElement(new[] { new { area_id = "room", patch = new { name = "Új név" } } }); break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.Handler.PrepareAsync(arguments, default).AsTask());
        Assert.Equal(0, f.Reads);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("order")]
    [InlineData("before")]
    [InlineData("patch")]
    [InlineData("changes")]
    [InlineData("owner")]
    public async Task HashBindsEveryMemberAndOrder(string change)
    {
        using var f = new BatchFixture();
        var operation = await f.Prepare(2);
        var members = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(operation.Parameters["items"]!)!;
        if (change == "order") members.Reverse();
        else if (change != "owner") members[1][change == "target" ? "entity_id" : change] += "changed";
        var other = new PreparedOperation(operation.Tool, operation.Risk, new Dictionary<string, string?> { ["items"] = JsonSerializer.Serialize(members) });
        Assert.NotEqual(operation.ComputeHash("Mikaela"), other.ComputeHash(change == "owner" ? "other" : "Mikaela"));
    }

    [Theory]
    [InlineData("prestate", 1, 1)]
    [InlineData("provider", 1, 2)]
    [InlineData("cancel", 1, 1)]
    [InlineData("kill", 1, 1)]
    [InlineData("audit_begin", 1, 1)]
    [InlineData("audit_complete", 1, 2)]
    public async Task FirstFailureStopsWithoutRollbackOrRetry(string defect, int completed, int writes)
    {
        using var f = new BatchFixture();
        var operation = await f.Prepare(3);
        using var cancellation = new CancellationTokenSource();
        var audit = new BatchAudit();
        switch (defect)
        {
            case "prestate": f.Names["sensor.item1"] = "Közben változott"; break;
            case "provider": f.FailTarget = "sensor.item1"; break;
            case "cancel": f.AfterWrite = () => { if (f.Writes.Count == 1) f.CancelAfterAudit = cancellation; }; break;
            case "kill": f.AfterWrite = () => f.Device.KillSwitch.Enabled = true; break;
            case "audit_begin": audit.FailBeginAt = 2; break;
            case "audit_complete": audit.FailCompleteAt = 2; break;
        }
        // Cancel csak az első tag lezárt auditja után, hogy az első tag igazoltan kész legyen.
        var store = new CancelAfterComplete(audit, f);
        var error = await Assert.ThrowsAsync<RegistryBatchExecutionException>(() => f.Handler.ExecuteAsync(operation,
            new(Guid.NewGuid(), "Mikaela", store, TimeProvider.System), cancellation.Token));
        Assert.Equal(completed, error.Progress.Completed);
        Assert.Equal(2, error.Progress.StoppedAt);
        Assert.Equal(1, error.Progress.NotStarted);
        Assert.Equal(writes, f.Writes.Count);
        Assert.DoesNotContain("sensor.item2", f.Writes);
        Assert.DoesNotContain("canary", error.Message);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("domain")]
    [InlineData("disabled_config")]
    public async Task ExistingPolicyCannotBeBypassedByItems(string defect)
    {
        using var f = new BatchFixture(enabled: defect != "disabled_config");
        var member = f.Member(0);
        if (defect != "disabled_config") member["entity_id"] = JsonSerializer.SerializeToElement(defect == "denied" ? "sensor.denied" : "switch.item0");
        await Assert.ThrowsAnyAsync<Exception>(() => f.Handler.PrepareAsync(Batch(member), default).AsTask());
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task EveryUnstartedMemberAlsoHasCorrelatedOldValueAudit()
    {
        using var f = new BatchFixture();
        var operation = await f.Prepare(3);
        f.Names["sensor.item1"] = "Megváltozott";
        var audit = new BatchAudit();
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<RegistryBatchExecutionException>(() => f.Handler.ExecuteAsync(operation, new(id, "Mikaela", audit, TimeProvider.System), default));
        Assert.Equal(3, audit.Calls.Count);
        Assert.Equal(new[] { "Succeeded", "Failed", "NotStarted" }, audit.Calls.Select(call => audit.States[call.CallId]));
        foreach (var call in audit.Calls)
        {
            using var row = JsonDocument.Parse(call.SanitizedParameters);
            Assert.Equal(id, row.RootElement.GetProperty("requestId").GetGuid());
            Assert.Contains("Szenzor", row.RootElement.GetProperty("parameters").GetProperty("before").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedBatchRechecksDeviceActiveEntitiesAtItsOwnTurn(bool deniedAfterFirstWrite)
    {
        using var f = new BatchFixture();
        var arguments = Batch(f.Member(0), HomeAssistantDeviceRegistryTests.Arguments("{\"name_by_user\":\"Új eszköz\"}"));
        var operation = await f.Handler.PrepareAsync(arguments, default);
        var audit = new BatchAudit();
        if (deniedAfterFirstWrite) f.AfterWrite = () => f.Device.ActiveResponse = "[\"sensor.denied\"]";
        if (deniedAfterFirstWrite)
        {
            var failure = await Assert.ThrowsAsync<RegistryBatchExecutionException>(() => f.Handler.ExecuteAsync(operation,
                new(Guid.NewGuid(), "Mikaela", audit, TimeProvider.System), default));
            Assert.Equal(1, failure.Progress.Completed);
            Assert.Equal(2, failure.Progress.StoppedAt);
            Assert.Equal(0, f.Device.Writes);
        }
        else
        {
            await f.Handler.ExecuteAsync(operation, new(Guid.NewGuid(), "Mikaela", audit, TimeProvider.System), default);
            Assert.Equal(1, f.Device.Writes);
            Assert.Equal("Új eszköz", f.Device.LastUpdate.GetProperty("name_by_user").GetString());
        }
        Assert.Single(f.Writes);
        Assert.Equal(2, audit.Calls.Count);
    }

    internal static ApprovalNotice Notice(PreparedOperation operation) => new(Guid.NewGuid(), "Mikaela", operation.Tool, operation.Risk,
        JsonSerializer.Serialize(operation.Parameters), new DateTimeOffset(2026, 11, 27, 20, 0, 0, TimeSpan.Zero), operation.Presentation);

    [Fact]
    public async Task OutcomePushIncludesPartialCountsAndStoppedPositionWithoutProviderText()
    {
        using var f = new BatchFixture();
        var notice = Notice(await f.Prepare(3));
        var progress = new ApprovalBatchProgress(3, 1, 2, "provider canary");
        var message = new ApprovalPushFormatter().Outcome(notice, ApprovalState.Failed, "canaryToken", "provider canary", progress);
        Assert.Contains("Elkészült: 1/3", message);
        Assert.Contains("megállt: 2", message);
        Assert.Contains("el nem indult: 1", message);
        Assert.DoesNotContain("provider canary", message);
    }

    internal sealed class BatchFixture : IDisposable
    {
        public HomeAssistantDeviceRegistryTests.Fixture Device { get; }
        public IApprovalOperationHandler Handler => Device.Operation;
        public List<string> Writes { get; } = [];
        public Dictionary<string, string> Names { get; } = [];
        public string? FailTarget { get; set; }
        public Action? AfterWrite { get; set; }
        public CancellationTokenSource? CancelAfterAudit { get; set; }
        public int Reads { get; private set; }
        public BatchFixture(bool enabled = true)
        {
            Device = new(enabled: enabled);
            var original = Device.Socket.RequestBody!;
            Device.Socket.RequestBody = command =>
            {
                var type = command.GetProperty("type").GetString();
                if (type == "config/entity_registry/get")
                {
                    Reads++;
                    var target = command.GetProperty("entity_id").GetString()!;
                    return JsonSerializer.Serialize(new { entity_id = target, device_id = (string?)null, area_id = (string?)null,
                        labels = Array.Empty<string>(), name = Names.GetValueOrDefault(target, "Szenzor"), icon = (string?)null,
                        hidden_by = (string?)null, disabled_by = (string?)null });
                }
                if (type == "config/entity_registry/update")
                {
                    var target = command.GetProperty("entity_id").GetString()!;
                    Writes.Add(target);
                    if (target == FailTarget) throw new InvalidOperationException("provider canary");
                    Names[target] = command.GetProperty("name").GetString()!;
                    AfterWrite?.Invoke();
                    return "{}";
                }
                return original(command);
            };
        }
        public Dictionary<string, JsonElement> Member(int index) => new()
        {
            ["entity_id"] = JsonSerializer.SerializeToElement("sensor.item" + index),
            ["patch"] = JsonSerializer.SerializeToElement(new { name = "Új név" })
        };
        public Dictionary<string, JsonElement> Arguments(int count) => Batch(Enumerable.Range(0, count).Select(Member).ToArray());
        public Task<PreparedOperation> Prepare(int count) => Handler.PrepareAsync(Arguments(count), default).AsTask();
        public void Dispose() => Device.Dispose();
    }

    private sealed class CancelAfterComplete(BatchAudit audit, BatchFixture fixture) : AgentGate.Audit.IAuditStore
    {
        public Task BeginAsync(AgentGate.Audit.AuditCall call, CancellationToken ct) => audit.BeginAsync(call, ct);
        public async Task CompleteAsync(Guid id, string state, double duration, string? error, CancellationToken ct)
        {
            await audit.CompleteAsync(id, state, duration, error, ct);
            fixture.CancelAfterAudit?.Cancel();
        }
        public Task CompletePendingAsync(Guid id, string state, string? error, CancellationToken ct) => audit.CompletePendingAsync(id, state, error, ct);
    }
}
