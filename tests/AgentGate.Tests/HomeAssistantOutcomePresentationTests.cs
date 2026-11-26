using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Integrations.HomeAssistant.Control;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Fact]
    public async Task RegistryOutcomePreservesEveryGraphemeOfLongButApprovedHumanChange()
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var before = string.Concat(Enumerable.Repeat("b\u0301", 40));
        var after = string.Concat(Enumerable.Repeat("x\u0301", 40));
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = before };
        var operation = await fixture.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments(JsonSerializer.Serialize(new { name = after })), default);
        var notice = Notice() with { Tool = operation.Tool, Risk = operation.Risk,
            Summary = JsonSerializer.Serialize(operation.Parameters), Presentation = operation.Presentation };
        await harness.Channel.SendRequestAsync(notice, default);
        await harness.Channel.SendOutcomeAsync(new(notice.Id, ApprovalState.Executed, null), default);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.EndsWith(operation.Presentation!.RegistryLine(), message);
        Assert.True(new System.Globalization.StringInfo(message.Split('\n')[1]).LengthInTextElements <= 176);
    }

    [Theory]
    [InlineData(ApprovalState.Rejected)]
    [InlineData(ApprovalState.Expired)]
    [InlineData(ApprovalState.Failed)]
    public async Task UnsuccessfulRegistryOutcomeDescribesOnlyRequestedChanges(ApprovalState state)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        using var fixture = new HomeAssistantRegistryTests.Fixture();
        var operation = await fixture.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"area_id\":\"room\",\"name\":\"Új\"}"), default);
        var notice = Notice() with { Tool = operation.Tool, Risk = operation.Risk,
            Summary = JsonSerializer.Serialize(operation.Parameters), Presentation = operation.Presentation };
        await harness.Channel.SendRequestAsync(notice, default);
        await harness.Channel.SendOutcomeAsync(new(notice.Id, state, "Kész, minden sikerült!"), default);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.DoesNotContain("Kész", message);
        Assert.Contains("Kért változás: Régi · Terület: Nincs → Szoba (saját); Név: Régi → Új", message);
    }

    [Theory]
    [InlineData("{\"area_id\":\"room\"}", "Terület: Nincs → Szoba (saját)")]
    [InlineData("{\"name\":\"Új\"}", "Név: Régi → Új")]
    [InlineData("{\"labels_add\":[\"new\"]}", "Címkék: Megmarad → Megmarad, Új")]
    [InlineData("{\"hidden_by\":\"user\"}", "Elrejtés: Nem → Igen")]
    [InlineData("{\"disabled_by\":\"user\"}", "Letiltás: Nem → Igen")]
    [InlineData("{\"area_id\":\"room\",\"name\":\"Új\",\"labels_add\":[\"new\"]}", "Terület: Nincs → Szoba (saját); Címkék: Megmarad → Megmarad, Új; Név: Régi → Új")]
    public async Task RegistryOutcomeContainsHumanTargetAndEveryRequestedChange(string patch, string expected)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        using var fixture = new HomeAssistantRegistryTests.Fixture();
        var operation = await fixture.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments(patch), default);
        var notice = Notice() with
        {
            Tool = operation.Tool, Risk = operation.Risk,
            Summary = JsonSerializer.Serialize(operation.Parameters), Presentation = operation.Presentation
        };
        await harness.Channel.SendRequestAsync(notice, default);
        await harness.Channel.SendOutcomeAsync(new(notice.Id, ApprovalState.Executed, "sensor.room area_id=room token=" + harness.Secrets.Token), default);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.Equal("Gate eredmény", body.RootElement.GetProperty("title").GetString());
        Assert.StartsWith("Kész:", message);
        Assert.Contains("Régi · " + expected, message);
        Assert.Equal(2, message.Split('\n').Length);
        Assert.True(message.Length <= 320);
        foreach (var forbidden in new[] { "sensor.room", "area_id", "ha_update_registry", notice.Id.ToString("N")[..8], harness.Secrets.Token, "Azonosító:", "token=" })
            Assert.DoesNotContain(forbidden, message);
        Assert.False(body.RootElement.TryGetProperty("data", out _));
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData(ApprovalState.Executed, "Kész:")]
    [InlineData(ApprovalState.Rejected, "Elutasítva:")]
    [InlineData(ApprovalState.Expired, "Lejárt:")]
    [InlineData(ApprovalState.Failed, "Sikertelen vagy bizonytalan:")]
    public async Task OutcomeStatusNeverClaimsSuccessOnFailureAndNeverEchoesRawResult(ApprovalState state, string prefix)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var notice = Notice() with { Presentation = new("Bejárat"), Tool = "ha_sensitive_control",
            Summary = JsonSerializer.Serialize(new { domain = "lock", service = "unlock", entity_id = "lock.frontdoor", data = "{}" }) };
        await harness.Channel.SendRequestAsync(notice, default);
        await harness.Channel.SendOutcomeAsync(new(notice.Id, state, "Kész! lock.frontdoor password=private\n" + harness.Secrets.Token), default);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.StartsWith(prefix, message);
        Assert.Contains("zár nyitása", message);
        Assert.Contains("Bejárat", message);
        Assert.DoesNotContain("lock.frontdoor", message);
        Assert.DoesNotContain("private", message);
        Assert.DoesNotContain(harness.Secrets.Token, message);
        if (state != ApprovalState.Executed) Assert.DoesNotContain("Kész", message);
        if (state == ApprovalState.Failed) Assert.Contains("Nem jelent visszagörgetést", message);
    }

    [Theory]
    [InlineData("sensor.technical_id")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("password=private")]
    [InlineData("Hamis\nKész")]
    public async Task UnsafeOutcomeTargetUsesNeutralHumanFallback(string target)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var notice = Notice() with { Presentation = new(target) };
        await harness.Channel.SendRequestAsync(notice, default);
        await harness.Channel.SendOutcomeAsync(new(notice.Id, ApprovalState.Executed, target), default);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("Kiválasztott eszköz", message);
        Assert.DoesNotContain(target, message);
        Assert.Equal(2, message.Split('\n').Length);
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("prefix", false)]
    [InlineData("suffix", false)]
    [InlineData("wrong-tool", false)]
    public async Task FailedOutcomeAcceptsOnlyAnExactClosedTtsCategory(string scenario, bool shown)
    {
        const string category = "A Home Assistant elutasította a felolvasást.";
        var known = new TtsExecutionException(TtsFailure.SpeechRejected).SafeMessage;
        var canary = "provider_canary_" + Guid.NewGuid().ToString("N");
        var result = scenario switch { "prefix" => canary + known, "suffix" => known + canary, _ => known };
        await using var harness = new Standalone();
        await harness.StartAsync();
        var notice = Notice() with { Presentation = new("Nappali"),
            Tool = scenario == "wrong-tool" ? "ha_control" : "ha_sensitive_control",
            Summary = JsonSerializer.Serialize(new { domain = "lock", service = "unlock", entity_id = "lock.frontdoor", data = "{}" }) };
        await harness.Channel.SendRequestAsync(notice, default);
        await harness.Channel.SendOutcomeAsync(new(notice.Id, ApprovalState.Failed, result), default);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.Equal(shown, message.Contains(category, StringComparison.Ordinal));
        Assert.DoesNotContain(canary, message);
        Assert.Contains("Nem jelent visszagörgetést", message);
    }

    [Fact]
    public async Task OutcomeRevalidatesPresentationAgainstCurrentTokenWithoutLosingStatus()
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var notice = Notice() with { Presentation = new(harness.Secrets.Token) };
        await harness.Channel.SendRequestAsync(notice, default);
        await harness.Channel.SendOutcomeAsync(new(notice.Id, ApprovalState.Executed, null), default);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.StartsWith("Kész:", message);
        Assert.Contains("Kiválasztott eszköz", message);
        Assert.DoesNotContain(harness.Secrets.Token, message);
    }
}
