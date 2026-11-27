using System.Text.Json;
using System.Text.Json.Nodes;
using AgentGate.Approval;
using AgentGate.Audit;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Integrations.HomeAssistant.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    // Az eredeti kérés; a válaszok és a katalógus kizárólag szintetikus mock adatok.
    private const string ExactLabelItems = """
        {"items":[
          {"create_label":{"name":"konyha"}},
          {"entity_id":"light.ikea_konyha_led","patch":{"labels_add_created":[0]}},
          {"create_label":{"name":"étkető"}},
          {"entity_id":"light.yeelight_etkezo","patch":{"labels_add_created":[2]}},
          {"entity_id":"light.yeelight_etkezo_led","patch":{"labels_add_created":[2]}}
        ]}
        """;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExactLabelItemsUseRealPreparationAndFullChannelWithoutEarlyRegistryWrites(int scenario)
    {
        using var registry = new ExactLabelRegistry(longLabels: scenario == 2);
        await using var harness = new Standalone();
        await harness.StartAsync();
        await using var app = new McpTestApplication("test-token", services =>
        {
            services.RemoveAll<IApprovalChannel>();
            services.AddSingleton<IApprovalChannel>(harness.Channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(new HomeAssistantRegistryOperation(registry.Service));
            services.PostConfigure<AuditOptions>(options => options.Caller = "Mikaela");
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        });
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", "test-token");
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http));
        var json = JsonNode.Parse(ExactLabelItems)!;
        if (scenario == 1)
        {
            foreach (var item in json["items"]!.AsArray().Where(item => item!["patch"] is not null))
                item!["patch"]!["labels_add"] = new JsonArray("keep");
        }
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(json.ToJsonString())!;
        var response = await client.CallToolAsync("ha_update_registry", arguments);
        Assert.True(response.IsError != true, string.Join(" ", response.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        Assert.Empty(registry.Writes);
        var requests = harness.Http.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("/api/services/persistent_notification/create", requests[0].Path);
        using var full = JsonDocument.Parse(requests[0].Body);
        var details = full.RootElement.GetProperty("message").GetString()!;
        foreach (var name in new[] { "konyha", "étkető", "IKEA Konyha LED", "Yeelight étkező", "Yeelight LED", registry.ExistingName })
            Assert.Contains(name, details);
        Assert.Contains("1. Új címke: konyha", details);
        Assert.Contains("3. Új címke: étkető", details);
        Assert.Contains("2. IKEA Konyha LED · Címkék:", details);
        Assert.Contains("4. Yeelight étkező · Címkék:", details);
        Assert.Contains("5. Yeelight LED · Címkék:", details);
        if (scenario == 2) Assert.Contains(registry.LastExistingName, details);
        using var push = JsonDocument.Parse(requests[1].Body);
        Assert.Equal("Jóváhagyás: 5 elem módosítása", push.RootElement.GetProperty("title").GetString());
        var message = push.RootElement.GetProperty("message").GetString()!;
        Assert.StartsWith("Mikaela kéri · lejár ", message);
        Assert.EndsWith("\nTeljes lista: Home Assistant értesítések.", message);
        Assert.DoesNotContain("konyha", message);
        Assert.DoesNotContain("light.", message);
        var id = Guid.ParseExact(full.RootElement.GetProperty("notification_id").GetString()!["agent_gate_".Length..], "N");
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        var status = gate.GetStatus(id)!;
        Assert.Equal(ApprovalState.Pending, status.State);
        Assert.Equal(5, status.Items!.Count);
        var statusText = await AgentGate.Tools.ApprovalTools.Status(id.ToString(), gate, default);
        foreach (var name in new[] { "konyha", "étkető", "IKEA Konyha LED", "Yeelight étkező", "label_refs", "CreateHash", "gate_pending_0", "gate_pending_2" })
            Assert.Contains(JsonEncodedText.Encode(name).ToString(), statusText);
        Assert.Contains(JsonEncodedText.Encode(registry.LastExistingName).ToString(), statusText);
        var (approve, _) = Codes(harness.Http);
        harness.Socket.Incoming.Writer.TryWrite(Event(approve));
        await WaitAsync(() => gate.GetStatus(id)!.State is ApprovalState.Executed or ApprovalState.Failed);
        Assert.Equal(ApprovalState.Executed, gate.GetStatus(id)!.State);
        Assert.Equal(5, registry.Writes.Count);
        Assert.Equal("konyha", registry.Writes[0].GetProperty("name").GetString());
        Assert.Equal("étkető", registry.Writes[2].GetProperty("name").GetString());
        Assert.Equal("light.ikea_konyha_led", registry.Writes[1].GetProperty("entity_id").GetString());
        Assert.Equal("light.yeelight_etkezo", registry.Writes[3].GetProperty("entity_id").GetString());
        Assert.Equal("light.yeelight_etkezo_led", registry.Writes[4].GetProperty("entity_id").GetString());
        Assert.Equal(registry.ExistingIds.Append("mock_created_0"), registry.Writes[1].GetProperty("labels").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(registry.ExistingIds.Append("mock_created_1"), registry.Writes[3].GetProperty("labels").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(registry.ExistingIds.Append("mock_created_1"), registry.Writes[4].GetProperty("labels").EnumerateArray().Select(value => value.GetString()));
    }

    [Theory]
    [InlineData("non-create")]
    [InlineData("forward")]
    [InlineData("self")]
    [InlineData("missing")]
    [InlineData("string")]
    public async Task ExactLabelItemsExplainInvalidReferencesThroughMcpWithoutNotificationOrWrite(string kind)
    {
        using var registry = new ExactLabelRegistry();
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
        var json = JsonNode.Parse(ExactLabelItems)!;
        if (kind == "non-create") json["items"]![3]!["patch"]!["labels_add_created"] = new JsonArray(1);
        else json["items"]![1]!["patch"]!["labels_add_created"] = kind switch
        {
            "forward" => new JsonArray(2), "self" => new JsonArray(1),
            "missing" => new JsonArray(49), _ => new JsonArray("0")
        };
        var response = await client.CallToolAsync("ha_update_registry", JsonSerializer.Deserialize<Dictionary<string, object?>>(json.ToJsonString())!);
        Assert.True(response.IsError);
        var text = string.Join(" ", response.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.Contains("labels_add_created", text);
        Assert.Contains("nulla alapú", text);
        Assert.Contains("items", text);
        Assert.Contains("korábbi create_label", text);
        Assert.DoesNotContain(ApprovalPresentation.SplitRequestMessage, text);
        Assert.Empty(registry.Writes);
        Assert.Empty(harness.Http.Requests);
    }

    [Theory]
    [InlineData("\"konyha\"", "\"étkető\"")]
    [InlineData("1", "2")]
    public async Task ExactLabelItemsRejectNamesAndOneBasedReferencesBeforeRegistryWrites(string kitchenReference, string diningReference)
    {
        using var registry = new ExactLabelRegistry();
        var json = ExactLabelItems.Replace("[2]", "[" + diningReference + "]", StringComparison.Ordinal)
            .Replace("[0]", "[" + kitchenReference + "]", StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<AgentGate.Tools.GateToolException>(() => registry.Service.PrepareAsync(HomeAssistantLabelCreationTests.Args(json), default));
        Assert.NotEqual(ApprovalPresentation.SplitRequestMessage, error.Message);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public async Task ExactLabelItemsRejectLabelNamesAsExistingIdsBeforeRegistryWrites()
    {
        using var registry = new ExactLabelRegistry();
        var json = ExactLabelItems.Replace("\"labels_add_created\":[0]", "\"labels_add\":[\"konyha\"]", StringComparison.Ordinal)
            .Replace("\"labels_add_created\":[2]", "\"labels_add\":[\"étkető\"]", StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<AgentGate.Tools.GateToolException>(() => registry.Service.PrepareAsync(HomeAssistantLabelCreationTests.Args(json), default));
        Assert.NotEqual(ApprovalPresentation.SplitRequestMessage, error.Message);
        Assert.Empty(registry.Writes);
    }

    private sealed class ExactLabelRegistry : IDisposable
    {
        private readonly JsonArray labels = [new JsonObject { ["label_id"] = "keep", ["name"] = "Megmarad" }];
        private readonly Dictionary<string, JsonNode> entities = new(StringComparer.Ordinal);
        private int created;
        public List<JsonElement> Writes { get; } = [];
        private readonly HomeAssistantClient client;
        public HomeAssistantRegistryService Service { get; }
        public string ExistingName => labels[0]!["name"]!.GetValue<string>();
        public string LastExistingName => labels[ExistingIds.Length - 1]!["name"]!.GetValue<string>();
        public string[] ExistingIds { get; }

        public ExactLabelRegistry(bool longLabels = false, bool suppliedNames = false, string? firstName = null)
        {
            // Hosszú, de megjeleníthető meglévő címkék; a lista túllépi a rövid push sorlimitjét.
            if (longLabels)
            {
                labels.Clear();
                for (var i = 0; i < 10; i++) labels.Add(new JsonObject
                {
                    ["label_id"] = $"keep_{i}", ["name"] = $"Hosszú meglévő szintetikus világításcímke {i}"
                });
            }
            if (suppliedNames) labels[0]!["name"] = "Lámpa";
            ExistingIds = labels.Select(label => label!["label_id"]!.GetValue<string>()).ToArray();
            foreach (var (id, name) in new[] { ("light.ikea_konyha_led", "IKEA Konyha LED"), ("light.yeelight_etkezo", "Yeelight étkező"), ("light.yeelight_etkezo_led", "Yeelight LED") })
                entities[id] = new JsonObject
                {
                    ["entity_id"] = id, ["name"] = name, ["device_id"] = null, ["area_id"] = null,
                    ["labels"] = JsonSerializer.SerializeToNode(ExistingIds), ["icon"] = null, ["hidden_by"] = null, ["disabled_by"] = null
                };
            if (suppliedNames)
            {
                // Valós nullable registry mezők; a device/label ID-k szintetikusak.
                // Mock-feltevés: a device nevek IKEA Konyha LED és Yeelight étkező.
                entities["light.ikea_konyha_led"]["name"] = null;
                entities["light.ikea_konyha_led"]["original_name"] = firstName ?? "IKEA Konyha LED";
                entities["light.yeelight_etkezo"]["name"] = null;
                entities["light.yeelight_etkezo"]["original_name"] = null;
                entities["light.yeelight_etkezo_led"]["name"] = "Led";
                entities["light.yeelight_etkezo_led"]["original_name"] = "Környezetfény";
                foreach (var entity in entities.Values)
                {
                    entity["area_id"] = "konyha";
                    entity["device_id"] = "mock_device_" + entity["entity_id"]!.GetValue<string>().Replace('.', '_');
                }
            }

            var secrets = new HomeAssistantReadToolsTests.RuntimeSecrets();
            var socket = new HomeAssistantLogTestSocketFactory(() => throw new InvalidOperationException(), () => secrets.Token)
            {
                RequestBody = command => command.GetProperty("type").GetString() switch
                {
                    "config/label_registry/list" => labels.ToJsonString(),
                    "config/entity_registry/get" => entities[command.GetProperty("entity_id").GetString()!].ToJsonString(),
                    "config/device_registry/list" => JsonSerializer.Serialize(entities.Values.Select(entity => new
                    { id = entity["device_id"]?.GetValue<string>(), area_id = "konyha",
                      name = entity["entity_id"]!.GetValue<string>() == "light.ikea_konyha_led" ? "IKEA Konyha LED" : "Yeelight étkező", name_by_user = (string?)null })),
                    "config/label_registry/create" => Create(command),
                    "config/entity_registry/update" => Update(command),
                    _ => throw new InvalidOperationException("Váratlan mock HA parancs.")
                }
            };
            client = new(Options.Create(new HomeAssistantOptions { BaseUrl = "https://ha.invalid/", TokenSecretId = Guid.NewGuid().ToString(), DomainAllowlist = ["light"] }),
                secrets, new RegistryDisplayHandler(id => suppliedNames ? id switch
                {
                    "light.ikea_konyha_led" => firstName ?? "IKEA Konyha LED IKEA Konyha LED",
                    "light.yeelight_etkezo" => "Yeelight étkező",
                    _ => "Yeelight étkező Led"
                } : entities[id]["name"]?.GetValue<string>()), socket);
            Service = new(client, RegistryPolicy.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["HomeAssistantRegistry:Enabled"] = "true" }).Build()), new HomeAssistantRegistryTests.MutableKillSwitch());
        }

        private string Create(JsonElement command)
        {
            Writes.Add(command.Clone());
            var row = new JsonObject
            {
                ["label_id"] = "mock_created_" + created++, ["name"] = command.GetProperty("name").GetString(),
                ["icon"] = null, ["color"] = null, ["description"] = null, ["created_at"] = 0, ["modified_at"] = 0
            };
            labels.Add(row.DeepClone());
            return row.ToJsonString();
        }

        private string Update(JsonElement command)
        {
            Writes.Add(command.Clone());
            var entity = entities[command.GetProperty("entity_id").GetString()!];
            entity["labels"] = JsonNode.Parse(command.GetProperty("labels").GetRawText());
            return entity.ToJsonString();
        }

        public void Dispose() => client.Dispose();
    }

    private sealed class RegistryDisplayHandler(Func<string, string?> name) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.StartsWith("/api/states/", request.RequestUri!.AbsolutePath);
            var id = request.RequestUri.AbsolutePath["/api/states/".Length..];
            var attributes = new Dictionary<string, object?>();
            if (name(id) is { } value) attributes["friendly_name"] = value;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { entity_id = id, state = "on", attributes }))
            });
        }
    }
}
