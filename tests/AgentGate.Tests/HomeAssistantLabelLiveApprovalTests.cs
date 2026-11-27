using System.Text;
using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Audit;
using AgentGate.Integrations.HomeAssistant.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    // A 2026-11-27 19:01 UTC-kor jelzett második kérés, nem az eredeti fixture átírása.
    private const string LiveSecondLabelItems = """
        {"items":[{"create_label":{"name":"konyha"}},{"create_label":{"name":"étkező"}},{"entity_id":"light.ikea_konyha_led","patch":{"labels_add_created":[0]}},{"entity_id":"light.yeelight_etkezo","patch":{"labels_add_created":[1]}},{"entity_id":"light.yeelight_etkezo_led","patch":{"labels_add_created":[1]}}]}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveSecondLabelItemsKeepSuppliedDisplayAndFullDetailsWithoutCompactBatchLimit(bool longLabels)
    {
        using var registry = new ExactLabelRegistry(longLabels: longLabels, suppliedNames: true);
        await using var harness = new Standalone();
        await harness.StartAsync();
        await using var app = new McpTestApplication("test-token", services =>
        {
            services.RemoveAll<IApprovalChannel>();
            services.AddSingleton<IApprovalChannel>(harness.Channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(new HomeAssistantRegistryOperation(registry.Service));
            services.PostConfigure<AuditOptions>(options => options.Caller = "Mikaela");
        });
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "test-token");
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http));
        var response = await client.CallToolAsync("ha_update_registry", JsonSerializer.Deserialize<Dictionary<string, object?>>(LiveSecondLabelItems)!);
        Assert.True(response.IsError != true, string.Join(" ", response.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        Assert.Empty(registry.Writes);
        var requests = harness.Http.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("/api/services/persistent_notification/create", requests[0].Path);
        Assert.Equal("/api/services/notify/mobile_app_test", requests[1].Path);
        using var full = JsonDocument.Parse(requests[0].Body);
        var details = full.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("1. Új címke: konyha", details);
        Assert.Contains("2. Új címke: étkező", details);
        Assert.InRange(Encoding.UTF8.GetByteCount(details), 1, 32_768);
        var id = Guid.ParseExact(full.RootElement.GetProperty("notification_id").GetString()!["agent_gate_".Length..], "N");
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        var status = gate.GetStatus(id)!;
        Assert.Equal(ApprovalState.Pending, status.State);
        Assert.Equal(5, status.Items!.Count);
        var statusText = await AgentGate.Tools.ApprovalTools.Status(id.ToString(), gate, default);
        var names = new[] { "IKEA Konyha LED IKEA Konyha LED", "Yeelight étkező", "Yeelight étkező Led" };
        for (var i = 0; i < names.Length; i++)
        {
            var item = status.Items[i + 2];
            Assert.Equal(names[i], item.Display.TargetName);
            Assert.Contains($"{i + 3}. {names[i]} · Címkék:", details);
            Assert.Contains(JsonEncodedText.Encode(names[i]).ToString(), statusText);
            Assert.Contains("Lámpa", item.Display.Changes["labels"].Before);
            Assert.Contains("Lámpa", item.Display.Changes["labels"].After);
            using var before = JsonDocument.Parse(item.Parameters["before"]!);
            Assert.Equal(i == 2 ? JsonValueKind.String : JsonValueKind.Null, before.RootElement.GetProperty("name").ValueKind);
            if (i == 2) Assert.Equal("Led", before.RootElement.GetProperty("name").GetString());
            Assert.Equal("konyha", before.RootElement.GetProperty("area_id").GetString());
            Assert.Equal("konyha", before.RootElement.GetProperty("device_area_id").GetString());
            Assert.Equal("konyha", before.RootElement.GetProperty("effective_area_id").GetString());
            if (longLabels)
            {
                // Ugyanez a teljes, engedett megjelenítés egytagú rövid pushként nem férne el.
                Assert.Throws<AgentGate.Tools.GateToolException>(() => item.Display.RegistryLine());
                Assert.Contains(registry.LastExistingName, item.Display.RegistryLine(extended: true));
                Assert.Contains(registry.LastExistingName, details);
            }
        }
        foreach (var name in new[] { "Lámpa", "konyha", "étkező", registry.LastExistingName })
            Assert.Contains(JsonEncodedText.Encode(name).ToString(), statusText);
        using var push = JsonDocument.Parse(requests[1].Body);
        Assert.Equal("Jóváhagyás: 5 elem módosítása", push.RootElement.GetProperty("title").GetString());
        var message = push.RootElement.GetProperty("message").GetString()!;
        Assert.StartsWith("Mikaela kéri · lejár ", message);
        Assert.EndsWith("\nTeljes lista: Home Assistant értesítések.", message);
        foreach (var name in names.Append("Lámpa").Append("light.")) Assert.DoesNotContain(name, message);
        var (approve, _) = Codes(harness.Http);
        harness.Socket.Incoming.Writer.TryWrite(Event(approve));
        await WaitAsync(() => gate.GetStatus(id)!.State is ApprovalState.Executed or ApprovalState.Failed);
        Assert.Equal(ApprovalState.Executed, gate.GetStatus(id)!.State);
        Assert.Equal(5, registry.Writes.Count);
        Assert.Equal("konyha", registry.Writes[0].GetProperty("name").GetString());
        Assert.Equal("étkező", registry.Writes[1].GetProperty("name").GetString());
        for (var i = 2; i < 5; i++)
            Assert.Equal(registry.ExistingIds.Append(i == 2 ? "mock_created_0" : "mock_created_1"),
                registry.Writes[i].GetProperty("labels").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task LiveSecondLabelItemsWithNullRegistryNamePrepareWithoutChangingBoundSnapshot()
    {
        using var registry = new ExactLabelRegistry(suppliedNames: true);
        var operation = await registry.Service.PrepareAsync(HomeAssistantLabelCreationTests.Args(LiveSecondLabelItems), default);
        Assert.Equal("IKEA Konyha LED IKEA Konyha LED", operation.Presentation!.Items![2].TargetName);
        Assert.Empty(registry.Writes);
    }

    [Theory]
    [InlineData("password=canary")]
    [InlineData("Ez a szintetikus név szándékosan meghaladja az ötven karaktert")]
    public async Task LiveSecondLabelItemsRejectUnsafeOrOverlongHumanNameBeforeAnyNotification(string name)
    {
        using var registry = new ExactLabelRegistry(suppliedNames: true, firstName: name);
        await using var harness = new Standalone();
        await harness.StartAsync();
        await using var app = new McpTestApplication("test-token", services =>
        {
            services.RemoveAll<IApprovalChannel>();
            services.AddSingleton<IApprovalChannel>(harness.Channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(new HomeAssistantRegistryOperation(registry.Service));
        });
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "test-token");
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http));
        var response = await client.CallToolAsync("ha_update_registry", JsonSerializer.Deserialize<Dictionary<string, object?>>(LiveSecondLabelItems)!);
        Assert.True(response.IsError);
        var text = string.Join(" ", response.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.DoesNotContain(name, text);
        Assert.Contains(name == "password=canary" ? "nem biztonságos vagy érvénytelen" : "túl hosszú", text);
        Assert.DoesNotContain(ApprovalPresentation.SplitRequestMessage, text);
        Assert.Empty(registry.Writes);
        Assert.Empty(harness.Http.Requests);
    }
}
