using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Integrations.HomeAssistant.Registry;
using AgentGate.Audit;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantRegistryBatchTests
{
    [Fact]
    public async Task ExecutionWritesInOrderAndAuditsEveryOldValueWithCommonRequest()
    {
        using var f = new HomeAssistantDeviceRegistryTests.Fixture();
        var old = f.Socket.RequestBody!;
        var writes = new List<string>();
        f.Socket.RequestBody = command => command.GetProperty("type").GetString() switch
        {
            "config/entity_registry/get" => JsonSerializer.Serialize(new { entity_id = "sensor.room", device_id = (string?)null, area_id = (string?)null,
                labels = Array.Empty<string>(), name = "Szenzor", icon = (string?)null, hidden_by = (string?)null, disabled_by = (string?)null }),
            "config/entity_registry/update" => EntityWrite(),
            "config/device_registry/update" => DeviceWrite(command),
            _ => old(command)
        };
        string EntityWrite() { writes.Add("sensor.room"); return "{}"; }
        string DeviceWrite(JsonElement command) { writes.Add("device"); return old(command); }
        var arguments = Batch(HomeAssistantRegistryTests.Arguments("{\"name\":\"Új szenzor\"}"),
            HomeAssistantDeviceRegistryTests.Arguments("{\"name_by_user\":\"Új eszköz\"}"));
        IApprovalOperationHandler handler = f.Operation;
        var operation = await handler.PrepareAsync(arguments, default);
        var audit = new BatchAudit();
        var requestId = Guid.NewGuid();
        var result = await handler.ExecuteAsync(operation, new(requestId, "Mikaela", audit, TimeProvider.System), default);
        Assert.Equal(new[] { "sensor.room", "device" }, writes);
        Assert.Contains("Elkészült: 2/2", result);
        Assert.Equal(2, audit.Calls.Count);
        Assert.All(audit.Calls, call =>
        {
            using var parameters = JsonDocument.Parse(call.SanitizedParameters);
            Assert.Equal(requestId, parameters.RootElement.GetProperty("requestId").GetGuid());
            Assert.True(parameters.RootElement.GetProperty("parameters").TryGetProperty("before", out _));
            Assert.Equal("Succeeded", audit.States[call.CallId]);
        });
    }

    internal static Dictionary<string, JsonElement> Batch(params Dictionary<string, JsonElement>[] members) => new() { ["items"] = JsonSerializer.SerializeToElement(members) };

    internal sealed class BatchAudit : IAuditStore
    {
        public List<AuditCall> Calls { get; } = [];
        public Dictionary<Guid, string> States { get; } = [];
        public int? FailBeginAt { get; set; }
        public int? FailCompleteAt { get; set; }
        public Task BeginAsync(AuditCall call, CancellationToken cancellationToken)
        {
            if (Calls.Count + 1 == FailBeginAt) throw new InvalidOperationException("provider canary");
            Calls.Add(call); return Task.CompletedTask;
        }
        public Task CompleteAsync(Guid callId, string state, double durationMilliseconds, string? sanitizedError, CancellationToken cancellationToken)
        {
            if (States.Count + 1 == FailCompleteAt) throw new InvalidOperationException("provider canary");
            States[callId] = state; return Task.CompletedTask;
        }
        public Task CompletePendingAsync(Guid callId, string state, string? sanitizedError, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    [Fact]
    public async Task PreparationBindsOrderedConcreteMembersAndAllOldValues()
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        var args = new Dictionary<string, JsonElement> { ["items"] = JsonSerializer.SerializeToElement(new[]
        {
            HomeAssistantRegistryTests.Arguments("{\"name\":\"Első\"}")
        }) };
        var op = await f.Operation.PrepareAsync(args, default);
        Assert.True(op.RequiresLosslessAudit);
        using var members = JsonDocument.Parse(op.Parameters["items"]!);
        var member = members.RootElement[0];
        Assert.Equal("sensor.room", member.GetProperty("entity_id").GetString());
        Assert.Contains("R", member.GetProperty("before").GetString());
        Assert.Contains("name", member.GetProperty("changes").GetString());
        Assert.NotNull(op.Presentation);
        Assert.Equal(0, f.Writes);
    }
}
