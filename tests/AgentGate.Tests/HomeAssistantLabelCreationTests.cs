using System.Text.Json;
using System.Text.Json.Nodes;
using AgentGate.Approval;
using AgentGate.Approval.HomeAssistant;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Integrations.HomeAssistant.Registry;
using AgentGate.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantLabelCreationTests
{
    internal static Dictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    private const string Create = """{"create_label":{"name":"Esti fény","icon":"mdi:tag","color":"blue"}}""";
    private const string Batch = """{"items":[{"create_label":{"name":"Esti fény"}},{"entity_id":"sensor.room","patch":{"labels_add_created":[0]}}]}""";

    [Fact]
    public async Task StandaloneCreateHasAbsenceDiffAndHumanPush()
    {
        using var f = new Fixture();
        var op = await f.Service.PrepareAsync(Args(Create), default);
        Assert.Equal(RiskLevel.Configuration, op.Risk);
        Assert.True(op.RequiresLosslessAudit);
        Assert.Equal("null", op.Parameters["before"]);
        var notice = new ApprovalNotice(Guid.NewGuid(), "Mikaela", op.Tool, op.Risk, JsonSerializer.Serialize(op.Parameters), DateTimeOffset.UtcNow.AddMinutes(2), op.Presentation);
        var push = new ApprovalPushFormatter(() => TimeZoneInfo.Utc).Format(notice, f.Secrets.Token);
        Assert.Equal("Jóváhagyás: új címke · Esti fény", push.Title);
        Assert.Contains("mdi:tag", push.Message);
        Assert.Contains("blue", push.Message);
        Assert.Empty(f.Writes);
        var result = await new HomeAssistantRegistryOperation(f.Service).ExecuteAsync(op, default);
        Assert.Contains("ha_generated_42", result);
        Assert.Equal("config/label_registry/create", Assert.Single(f.Writes).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":\" \"}")]
    [InlineData("{\"name\":\"Esti fény\",\"description\":\"nem\"}")]
    [InlineData("{\"name\":\"Esti fény\",\"icon\":\"https://icon\"}")]
    [InlineData("{\"name\":\"Esti fény\",\"color\":\"var(--x)\"}")]
    [InlineData("{\"name\":\"Esti fény\",\"name\":\"Más\"}")]
    public async Task InvalidCreateIsDeniedWithoutWrite(string create)
    {
        using var f = new Fixture();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.PrepareAsync(Args("{\"create_label\":" + create + "}"), default));
        Assert.Empty(f.Writes);
    }

    [Theory]
    [InlineData("ESTIFÉNY")]
    [InlineData("Esti fe\u0301ny")]
    [InlineData("Esti   fény")]
    public async Task EquivalentExistingNameIsDeniedAtPreparation(string name)
    {
        using var f = new Fixture();
        f.Labels.Add(new JsonObject { ["label_id"] = "existing", ["name"] = name });
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.PrepareAsync(Args(Create), default));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task CollisionArrivingAfterApprovalStopsBeforeCreate()
    {
        using var f = new Fixture();
        var op = await f.Service.PrepareAsync(Args(Create), default);
        f.Labels.Add(new JsonObject { ["label_id"] = "other", ["name"] = "ESTIFÉNY" });
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.ExecuteAsync(op, default));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task BatchResolvesOnlyValidatedServerIdAndAuditsIt()
    {
        using var f = new Fixture();
        var op = await f.Service.PrepareAsync(Args(Batch), default);
        Assert.Equal(2, op.Presentation!.Items!.Count);
        var notice = new ApprovalNotice(Guid.NewGuid(), "Mikaela", op.Tool, op.Risk, JsonSerializer.Serialize(op.Parameters), DateTimeOffset.UtcNow.AddMinutes(2), op.Presentation);
        var formatter = new ApprovalPushFormatter(() => TimeZoneInfo.Utc);
        Assert.Equal("Jóváhagyás: 2 elem módosítása", formatter.Format(notice, f.Secrets.Token).Title);
        Assert.Contains("új címke", formatter.RegistryBatchDetails(notice, f.Secrets.Token));
        var audit = new HomeAssistantRegistryBatchTests.BatchAudit();
        await f.Service.ExecuteBatchAsync(op, new(Guid.NewGuid(), "Mikaela", audit, TimeProvider.System), default);
        Assert.Equal(2, f.Writes.Count);
        Assert.Equal("ha_generated_42", f.Writes[1].GetProperty("labels")[0].GetString());
        Assert.Contains(audit.Calls, call => call.SanitizedParameters.Contains("ha_generated_42") && call.SanitizedParameters.Contains("createHash"));
    }

    [Theory]
    [InlineData("{\"items\":[{\"entity_id\":\"sensor.room\",\"patch\":{\"labels_add_created\":[1]}},{\"create_label\":{\"name\":\"Esti fény\"}}]}")]
    [InlineData("{\"items\":[{\"create_label\":{\"name\":\"Esti fény\"}},{\"entity_id\":\"sensor.room\",\"patch\":{\"labels_add_created\":[1]}}]}")]
    [InlineData("{\"items\":[{\"create_label\":{\"name\":\"Esti fény\"}},{\"entity_id\":\"sensor.room\",\"patch\":{\"labels_add_created\":[9]}}]}")]
    [InlineData("{\"items\":[{\"create_label\":{\"name\":\"Esti fény\"}},{\"entity_id\":\"sensor.room\",\"patch\":{\"labels_add_created\":[0,0]}}]}")]
    [InlineData("{\"entity_id\":\"sensor.room\",\"patch\":{\"labels_add_created\":[0]}}")]
    public async Task MissingForwardSelfDuplicateAndStandaloneReferencesAreDenied(string json)
    {
        using var f = new Fixture();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.PrepareAsync(Args(json), default));
        Assert.Empty(f.Writes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"label_id\":\"bad/id\",\"name\":\"Esti fény\",\"icon\":null,\"color\":null,\"description\":null,\"created_at\":0,\"modified_at\":0}")]
    [InlineData("{\"label_id\":\"id\",\"name\":\"Más név\",\"icon\":null,\"color\":null,\"description\":null,\"created_at\":0,\"modified_at\":0}")]
    [InlineData("{\"label_id\":\"id\",\"label_id\":\"other\",\"name\":\"Esti fény\"}")]
    public async Task UncertainOrInvalidCreateNeverAssigns(string response)
    {
        using var f = new Fixture { CreateResponse = response };
        var op = await f.Service.PrepareAsync(Args(Batch), default);
        var error = await Assert.ThrowsAsync<RegistryBatchExecutionException>(() => f.Service.ExecuteBatchAsync(op,
            new(Guid.NewGuid(), "Mikaela", new HomeAssistantRegistryBatchTests.BatchAudit(), TimeProvider.System), default));
        Assert.Equal(0, error.Progress.Completed);
        Assert.Single(f.Writes);
    }

    [Fact]
    public async Task ExternalCatalogChangeAfterCreateDoesNotReceiveBlanketExemption()
    {
        using var f = new Fixture { AfterCreate = labels => labels.Add(new JsonObject { ["label_id"] = "outsider", ["name"] = "Idegen" }) };
        var op = await f.Service.PrepareAsync(Args(Batch), default);
        var error = await Assert.ThrowsAsync<RegistryBatchExecutionException>(() => f.Service.ExecuteBatchAsync(op,
            new(Guid.NewGuid(), "Mikaela", new HomeAssistantRegistryBatchTests.BatchAudit(), TimeProvider.System), default));
        Assert.Equal(1, error.Progress.Completed);
        Assert.Single(f.Writes);
        Assert.Contains(f.Labels, node => node!["label_id"]!.GetValue<string>() == "ha_generated_42");
    }

    [Fact]
    public async Task KillSwitchBlocksCreate()
    {
        using var f = new Fixture();
        var op = await f.Service.PrepareAsync(Args(Create), default);
        f.KillSwitch.Enabled = true;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.ExecuteAsync(op, default));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task UnicodeFullCaseFoldCollisionIsDenied()
    {
        using var f = new Fixture();
        f.Labels.Add(new JsonObject { ["label_id"] = "existing", ["name"] = "Straße" });
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.PrepareAsync(Args("""{"create_label":{"name":"STRASSE"}}"""), default));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task CreateCountsTowardsBatchLimitAndDuplicateNamesAreDenied()
    {
        using var f = new Fixture();
        var items = Enumerable.Range(0, 51).Select(i => new { create_label = new { name = $"Címke {i}" } });
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.PrepareAsync(Args(JsonSerializer.Serialize(new { items })), default));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Service.PrepareAsync(Args("""{"items":[{"create_label":{"name":"Esti fény"}},{"create_label":{"name":"ESTIFÉNY"}}]}"""), default));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task DeviceAssignmentKeepsExplicitExistingLabels()
    {
        using var f = new Fixture();
        f.Labels.Add(new JsonObject { ["label_id"] = "keep", ["name"] = "Megmarad" });
        var op = await f.Service.PrepareAsync(Args("""{"items":[{"create_label":{"name":"Esti fény"}},{"device_id":"device","patch":{"labels":["keep"],"labels_add_created":[0]}}]}"""), default);
        await f.Service.ExecuteBatchAsync(op, new(Guid.NewGuid(), "Mikaela", new HomeAssistantRegistryBatchTests.BatchAudit(), TimeProvider.System), default);
        Assert.Equal(new[] { "ha_generated_42", "keep" }, f.Writes[1].GetProperty("labels").EnumerateArray().Select(label => label.GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ExistingCatalogMetadataChangeAfterCreateStopsAssignment()
    {
        using var f = new Fixture { AfterCreate = labels => labels[0]!["color"] = "red" };
        f.Labels.Add(new JsonObject { ["label_id"] = "keep", ["name"] = "Megmarad", ["color"] = "blue" });
        var op = await f.Service.PrepareAsync(Args(Batch), default);
        var error = await Assert.ThrowsAsync<RegistryBatchExecutionException>(() => f.Service.ExecuteBatchAsync(op,
            new(Guid.NewGuid(), "Mikaela", new HomeAssistantRegistryBatchTests.BatchAudit(), TimeProvider.System), default));
        Assert.Equal(1, error.Progress.Completed);
        Assert.Single(f.Writes);
    }

    [Fact]
    public async Task LostCreateResponseStopsWithoutRetryOrAssignment()
    {
        using var f = new Fixture { AfterCreate = _ => throw new System.Net.WebSockets.WebSocketException() };
        var op = await f.Service.PrepareAsync(Args(Batch), default);
        var error = await Assert.ThrowsAsync<RegistryBatchExecutionException>(() => f.Service.ExecuteBatchAsync(op,
            new(Guid.NewGuid(), "Mikaela", new HomeAssistantRegistryBatchTests.BatchAudit(), TimeProvider.System), default));
        Assert.Equal(0, error.Progress.Completed);
        Assert.Single(f.Writes);
        Assert.Single(f.Labels);
    }

    [Fact]
    public async Task KillSwitchAfterCreatePreservesLabelWithoutAssignment()
    {
        using var f = new Fixture();
        f.AfterCreate = _ => f.KillSwitch.Enabled = true;
        var op = await f.Service.PrepareAsync(Args(Batch), default);
        var error = await Assert.ThrowsAsync<RegistryBatchExecutionException>(() => f.Service.ExecuteBatchAsync(op,
            new(Guid.NewGuid(), "Mikaela", new HomeAssistantRegistryBatchTests.BatchAudit(), TimeProvider.System), default));
        Assert.Equal(1, error.Progress.Completed);
        Assert.Single(f.Writes);
    }

    [Fact]
    public async Task DisabledRegistryAndDeniedDependentEntityCannotWrite()
    {
        using var disabled = new Fixture(enabled: false);
        await Assert.ThrowsAnyAsync<Exception>(() => disabled.Service.PrepareAsync(Args(Create), default));
        using var denied = new Fixture(deny: "sensor.room");
        await Assert.ThrowsAnyAsync<Exception>(() => denied.Service.PrepareAsync(Args(Batch), default));
        Assert.Empty(disabled.Writes);
        Assert.Empty(denied.Writes);
    }

    [Fact]
    public async Task CancelledExecutionCannotCreate()
    {
        using var f = new Fixture();
        var op = await f.Service.PrepareAsync(Args(Create), default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.ExecuteAsync(op, cancellation.Token));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public async Task TwoCreatesResolveTheSpecifiedPriorOperationOnly()
    {
        using var f = new Fixture();
        var op = await f.Service.PrepareAsync(Args("""{"items":[{"create_label":{"name":"Első"}},{"create_label":{"name":"Második"}},{"entity_id":"sensor.room","patch":{"labels_add_created":[1]}}]}"""), default);
        await f.Service.ExecuteBatchAsync(op, new(Guid.NewGuid(), "Mikaela", new HomeAssistantRegistryBatchTests.BatchAudit(), TimeProvider.System), default);
        Assert.Equal(3, f.Writes.Count);
        Assert.Equal("ha_generated_43", Assert.Single(f.Writes[2].GetProperty("labels").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task TamperedReferenceHashNeverAssigns()
    {
        using var f = new Fixture();
        var op = await f.Service.PrepareAsync(Args(Batch), default);
        var items = JsonNode.Parse(op.Parameters["items"]!)!.AsArray();
        var refs = JsonNode.Parse(items[1]!["label_refs"]!.GetValue<string>())!;
        refs[0]!["CreateHash"] = new string('0', 64);
        items[1]!["label_refs"] = refs.ToJsonString();
        var changed = new PreparedOperation(op.Tool, op.Risk, new Dictionary<string, string?> { ["items"] = items.ToJsonString() }, requiresLosslessAudit: true);
        Assert.NotEqual(op.ComputeHash("Mikaela"), changed.ComputeHash("Mikaela"));
        var error = await Assert.ThrowsAsync<RegistryBatchExecutionException>(() => f.Service.ExecuteBatchAsync(changed,
            new(Guid.NewGuid(), "Mikaela", new HomeAssistantRegistryBatchTests.BatchAudit(), TimeProvider.System), default));
        Assert.Equal(1, error.Progress.Completed);
        Assert.Single(f.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvedIdIsReorderedAcrossAnExistingLabel(bool device)
    {
        using var f = new Fixture { EntityLabelIds = ["keep"] };
        f.Labels.Add(new JsonObject { ["label_id"] = "keep", ["name"] = "Megmarad" });
        f.CreatedIds.Enqueue("alarm");
        var target = device ? "\"device_id\":\"device\",\"patch\":{\"labels\":[\"keep\"],\"labels_add_created\":[0]}" :
            "\"entity_id\":\"sensor.room\",\"patch\":{\"labels_add_created\":[0]}";
        var op = await f.Service.PrepareAsync(Args("{\"items\":[{\"create_label\":{\"name\":\"Alarm\"}},{" + target + "}]}"), default);
        var hash = op.ComputeHash("Mikaela");
        await f.Service.ExecuteBatchAsync(op, new(Guid.NewGuid(), "Mikaela", new HomeAssistantRegistryBatchTests.BatchAudit(), TimeProvider.System), default);
        Assert.Equal(new[] { "alarm", "keep" }, f.Writes[1].GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
        Assert.Equal(hash, op.ComputeHash("Mikaela"));
    }

    [Fact]
    public async Task TwoReversedServerIdsAreCanonicalizedAfterResolution()
    {
        using var f = new Fixture();
        f.CreatedIds.Enqueue("zeta");
        f.CreatedIds.Enqueue("alpha");
        var op = await f.Service.PrepareAsync(Args("""{"items":[{"create_label":{"name":"Zeta"}},{"create_label":{"name":"Alpha"}},{"entity_id":"sensor.room","patch":{"labels_add_created":[0,1]}}]}"""), default);
        await f.Service.ExecuteBatchAsync(op, new(Guid.NewGuid(), "Mikaela", new HomeAssistantRegistryBatchTests.BatchAudit(), TimeProvider.System), default);
        Assert.Equal(new[] { "alpha", "zeta" }, f.Writes[2].GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
    }

    internal sealed class Fixture : IDisposable
    {
        private int createCount;
        public JsonArray Labels { get; } = [];
        public Queue<string> CreatedIds { get; } = [];
        public string[] EntityLabelIds { get; init; } = [];
        public List<JsonElement> Writes { get; } = [];
        public string? CreateResponse { get; set; }
        public Action<JsonArray>? AfterCreate { get; set; }
        public HomeAssistantReadToolsTests.RuntimeSecrets Secrets { get; } = new();
        public HomeAssistantRegistryTests.MutableKillSwitch KillSwitch { get; } = new();
        public HomeAssistantClient Client { get; }
        public HomeAssistantRegistryService Service { get; }
        public Fixture(bool enabled = true, string? deny = null)
        {
            var socket = new HomeAssistantLogTestSocketFactory(() => throw new InvalidOperationException(), () => Secrets.Token)
            {
                RequestBody = command => command.GetProperty("type").GetString() switch
                {
                    "config/label_registry/list" => Labels.ToJsonString(),
                    "config/label_registry/create" => CreateLabel(command),
                    "config/entity_registry/get" => EntityRecord(),
                    "config/entity_registry/update" => Update(command),
                    "config/device_registry/list" => DeviceList(),
                    "config/device_registry/update" => Update(command),
                    _ => throw new InvalidOperationException("Váratlan parancs.")
                }
            };
            var handler = new HomeAssistantReadToolsTests.Handler("[]")
            {
                Respond = (request, _) =>
                {
                    Assert.Equal("/api/template", request.RequestUri!.AbsolutePath);
                    return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[\"sensor.room\"]") });
                }
            };
            Client = new(Options.Create(new HomeAssistantOptions { BaseUrl = "https://ha.example/", TokenSecretId = Guid.NewGuid().ToString(), DomainAllowlist = ["sensor"] }), Secrets, handler, socket);
            var values = new Dictionary<string, string?> { ["HomeAssistantRegistry:Enabled"] = enabled ? "true" : "false" };
            if (deny is not null) values["HomeAssistantRegistry:EntityDenylist:0"] = deny;
            Service = new(Client, RegistryPolicy.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build()), KillSwitch);
        }
        private string EntityRecord()
        {
            var entity = JsonNode.Parse("""{"entity_id":"sensor.room","device_id":null,"area_id":null,"labels":[],"name":"Szobai fény","icon":null,"hidden_by":null,"disabled_by":null}""")!;
            entity["labels"] = JsonSerializer.SerializeToNode(EntityLabelIds);
            return entity.ToJsonString();
        }
        private static string DeviceList()
        {
            var device = HomeAssistantDeviceRegistryTests.DeviceRecord();
            device["labels"] = new JsonArray("keep");
            return new JsonArray(device).ToJsonString();
        }
        private string CreateLabel(JsonElement command)
        {
            Writes.Add(command.Clone());
            if (CreateResponse is not null) return CreateResponse;
            var entry = new JsonObject { ["label_id"] = CreatedIds.Count > 0 ? CreatedIds.Dequeue() : $"ha_generated_{42 + createCount++}", ["name"] = command.GetProperty("name").GetString(),
                ["icon"] = command.TryGetProperty("icon", out var icon) ? JsonNode.Parse(icon.GetRawText()) : null,
                ["color"] = command.TryGetProperty("color", out var color) ? JsonNode.Parse(color.GetRawText()) : null,
                ["description"] = null, ["created_at"] = 0, ["modified_at"] = 0 };
            Labels.Add(entry.DeepClone());
            AfterCreate?.Invoke(Labels);
            return entry.ToJsonString();
        }
        private string Update(JsonElement command) { Writes.Add(command.Clone()); return "{}"; }
        public void Dispose() => Client.Dispose();
    }
}
