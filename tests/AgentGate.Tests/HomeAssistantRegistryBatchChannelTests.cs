using System.Net;
using System.Text.Json;
using AgentGate.Approval;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Fact]
    public async Task RegistryBatchPostsOneFullPersistentListBeforeShortPushAndUsesExistingDecisionChannel()
    {
        using var registry = new HomeAssistantRegistryBatchTests.BatchFixture();
        var operation = await registry.Prepare(50);
        await using var harness = new Standalone();
        await harness.StartAsync();
        var notice = HomeAssistantRegistryBatchTests.Notice(operation) with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        await harness.Channel.SendRequestAsync(notice, default);
        var requests = harness.Http.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("/api/services/persistent_notification/create", requests[0].Path);
        using var full = JsonDocument.Parse(requests[0].Body);
        var message = full.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("1. Szenzor", message);
        Assert.Contains("50. Szenzor", message);
        Assert.Contains("sensor.item49", message);
        Assert.Equal("agent_gate_" + notice.Id.ToString("N"), full.RootElement.GetProperty("notification_id").GetString());
        Assert.Equal("/api/services/notify/mobile_app_test", requests[1].Path);
        Assert.DoesNotContain("sensor.item", requests[1].Body);
        var (approve, reject) = Codes(harness.Http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await using var reader = harness.Channel.ReadDecisionsAsync(timeout.Token).GetAsyncEnumerator();
        harness.Socket.Incoming.Writer.TryWrite(Event(approve));
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(notice.Id, reader.Current.Id);
        harness.Socket.Incoming.Writer.TryWrite(Event(reject));
        await harness.Channel.SendOutcomeAsync(new(notice.Id, ApprovalState.Failed, "provider canary", Progress: new(50, 2, 3, "closed")), default);
        using var result = JsonDocument.Parse(harness.Http.Body!);
        Assert.Contains("Elkészült: 2/50", result.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("provider canary", harness.Http.Body);
    }

    [Fact]
    public async Task RegistryBatchPersistentFailurePreventsActionPushAndRevokesGrant()
    {
        using var registry = new HomeAssistantRegistryBatchTests.BatchFixture();
        var operation = await registry.Prepare(2);
        await using var harness = new Standalone();
        await harness.StartAsync();
        harness.Http.Status = HttpStatusCode.InternalServerError;
        var notice = HomeAssistantRegistryBatchTests.Notice(operation) with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Channel.SendRequestAsync(notice, default));
        Assert.Equal("/api/services/persistent_notification/create", Assert.Single(harness.Http.Requests).Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Channel.SendOutcomeAsync(new(notice.Id, ApprovalState.Failed, null), default));
        Assert.Equal(1, harness.Http.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("secret")]
    [InlineData("oversized")]
    public async Task RegistryBatchIncompleteOrUnsafePresentationNeverSendsAnything(string defect)
    {
        using var registry = new HomeAssistantRegistryBatchTests.BatchFixture();
        var operation = await registry.Prepare(defect == "oversized" ? 50 : 2);
        var displays = operation.Presentation!.Items!.ToArray();
        if (defect == "missing") displays = displays.Take(1).ToArray();
        else if (defect == "secret") displays[1] = new("password=canary", displays[1].Changes);
        else
        {
            // Érvényes nevekből álló, de a teljes értesítési keretbe nem férő lista.
            var names = string.Join(", ", Enumerable.Repeat(new string('a', 48), 16));
            displays = displays.Select(display => new ApprovalPresentation("Szenzor", new Dictionary<string, ApprovalDisplayChange>
            { ["name"] = new(names, names) })).ToArray();
        }
        var notice = HomeAssistantRegistryBatchTests.Notice(operation) with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5), Presentation = new(null, items: displays)
        };
        await using var harness = new Standalone();
        await harness.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Channel.SendRequestAsync(notice, default));
        Assert.Equal(0, harness.Http.Count);
    }
}
