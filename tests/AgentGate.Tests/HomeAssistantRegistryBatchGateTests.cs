using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AgentGate.Approval;
using AgentGate.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantRegistryBatchGateTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("partial")]
    [InlineData("original_audit_failure")]
    [InlineData("package_audit_failure")]
    public async Task McpReturnsFullOwnedListAndPublishesFinalProgressOnlyAfterAllAudits(string scenario)
    {
        using var registry = new HomeAssistantRegistryBatchTests.BatchFixture();
        var channel = new BatchChannel();
        var audit = new GateAudit { FailOriginal = scenario == "original_audit_failure", FailPackage = scenario == "package_audit_failure" };
        await using var app = new McpTestApplication("test-token", services =>
        {
            services.RemoveAll<IApprovalChannel>();
            services.AddSingleton<IApprovalChannel>(channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(registry.Handler);
            services.RemoveAll<IAuditStore>();
            services.AddSingleton<IAuditStore>(audit);
        });
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "test-token");
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http));
        var items = Enumerable.Range(0, 3).Select(i => new { entity_id = "sensor.item" + i, patch = new { name = "Új név" } }).ToArray();
        var response = await client.CallToolAsync("ha_update_registry", new Dictionary<string, object?> { ["items"] = items });
        Assert.True(response.IsError != true, Text(response));
        var id = Assert.Single(channel.Notices).Id;
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        var pending = gate.GetStatus(id)!;
        Assert.Equal(3, pending.Items!.Count);
        Assert.Contains("sensor.item2", await AgentGate.Tools.ApprovalTools.Status(id.ToString(), gate, default));
        var before = await client.CallToolAsync("gate_approval_status", new Dictionary<string, object?> { ["requestId"] = id.ToString() });
        Assert.Contains("sensor.item2", Text(before));
        Assert.Contains("Szenzor", Text(before));
        var owner = app.Services.GetRequiredService<IOptions<AuditOptions>>().Value;
        var originalOwner = owner.Caller;
        owner.Caller = "other-caller";
        Assert.Null(gate.GetStatus(id));
        owner.Caller = originalOwner;
        Assert.Null(gate.GetStatus(Guid.NewGuid()));
        if (scenario == "partial") registry.Names["sensor.item1"] = "Közben változott";
        channel.Decide(id);
        await audit.Closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ApprovalState.Approved, gate.GetStatus(id)!.State);
        Assert.Equal(0, gate.GetStatus(id)!.Progress!.Completed);
        Assert.Equal(3, gate.GetStatus(id)!.Progress!.NotStarted);
        var wait = gate.GetStatusAsync(id, 10, default);
        Assert.False(wait.IsCompleted);
        Assert.False(channel.Outcome.Task.IsCompleted);
        audit.Release.TrySetResult();
        var status = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(status);
        Assert.Equal(scenario == "success" ? ApprovalState.Executed : ApprovalState.Failed, status.State);
        Assert.Equal(scenario == "partial" ? 1 : 3, status.Progress!.Completed);
        Assert.Equal(scenario == "partial" ? 1 : 0, status.Progress.NotStarted);
        var outcome = await channel.Outcome.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(status.Progress, outcome.Progress);
        Assert.Equal(3, outcome.Items!.Count);
        var final = await client.CallToolAsync("gate_approval_status", new Dictionary<string, object?> { ["requestId"] = id.ToString() });
        Assert.Contains("sensor.item2", Text(final));
        Assert.DoesNotContain("provider-canary", Text(final));
        Assert.Contains(audit.Calls, row => row.SanitizedParameters.Contains("prepared", StringComparison.Ordinal) && row.SanitizedParameters.Contains("operationHash", StringComparison.Ordinal));
        var memberRows = audit.Calls.Where(row => row.SanitizedParameters.Contains("batch_item", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, memberRows.Length);
        foreach (var row in memberRows)
        {
            using var json = JsonDocument.Parse(row.SanitizedParameters);
            Assert.Equal(id, json.RootElement.GetProperty("requestId").GetGuid());
            Assert.Contains("Szenzor", json.RootElement.GetProperty("parameters").GetProperty("before").GetString());
        }
        channel.Decide(id);
        Assert.Equal(scenario == "partial" ? 1 : 3, registry.Writes.Count);
    }

    private static string Text(CallToolResult result) => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private sealed class BatchChannel : IApprovalChannel
    {
        private readonly Channel<ApprovalDecision> decisions = System.Threading.Channels.Channel.CreateUnbounded<ApprovalDecision>();
        public string Name => "batch-test";
        public bool IsAvailable => true;
        public ConcurrentQueue<ApprovalNotice> Notices { get; } = new();
        public TaskCompletionSource<ApprovalOutcome> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendRequestAsync(ApprovalNotice notice, CancellationToken cancellationToken) { Notices.Enqueue(notice); return Task.CompletedTask; }
        public Task SendOutcomeAsync(ApprovalOutcome outcome, CancellationToken cancellationToken) { Outcome.TrySetResult(outcome); return Task.CompletedTask; }
        public void Decide(Guid id) => decisions.Writer.TryWrite(new(id, ApprovalChoice.Approve, "tester", DateTimeOffset.UtcNow));
        public async IAsyncEnumerable<ApprovalDecision> ReadDecisionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        { await foreach (var decision in decisions.Reader.ReadAllAsync(cancellationToken)) yield return decision; }
    }

    private sealed class GateAudit : IAuditStore
    {
        public bool FailOriginal;
        public bool FailPackage;
        public ConcurrentQueue<AuditCall> Calls { get; } = new();
        private readonly ConcurrentDictionary<Guid, bool> executions = new();
        public TaskCompletionSource Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task BeginAsync(AuditCall call, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Enqueue(call);
            using var json = JsonDocument.Parse(call.SanitizedParameters);
            if (json.RootElement.TryGetProperty("phase", out var phase) && phase.GetString() == "execution") executions[call.CallId] = true;
            return Task.CompletedTask;
        }
        public Task CompleteAsync(Guid id, string state, double elapsed, string? error, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (FailPackage && executions.ContainsKey(id)) throw new IOException("provider-canary");
            return Task.CompletedTask;
        }
        public async Task CompletePendingAsync(Guid id, string state, string? error, CancellationToken ct)
        {
            Closing.TrySetResult();
            await Release.Task.WaitAsync(ct);
            if (FailOriginal) throw new IOException("provider-canary");
        }
    }
}
