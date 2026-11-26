using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentGate.Approval;
using AgentGate.Approval.HomeAssistant;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Integrations.HomeAssistant.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantDeviceRegistryTests
{
    [Fact]
    public async Task DeviceApprovalAndOutcomeAreHumanAndComplete()
    {
        using var f = new Fixture();
        var operation = await f.Prepare("""{"name_by_user":"Új"}""");
        var notice = new ApprovalNotice(Guid.NewGuid(), "Mikaela", operation.Tool, operation.Risk,
            JsonSerializer.Serialize(operation.Parameters), DateTimeOffset.UtcNow.AddMinutes(2), operation.Presentation);
        var formatter = new ApprovalPushFormatter(() => TimeZoneInfo.Utc);
        var push = formatter.Format(notice, f.Secrets.Token);
        Assert.Contains("név módosítása", push.Title);
        Assert.Contains("Régi → Új", push.Message);
        Assert.DoesNotContain("device_id", push.Message);
        var outcome = formatter.Outcome(notice, ApprovalState.Executed, f.Secrets.Token);
        Assert.Contains("Kész", outcome);
        Assert.Contains("Régi → Új", outcome);
    }

    [Fact]
    public async Task DeviceUsesFixedActiveEntityTemplateAndApprovalBoundUpdate()
    {
        using var f = new Fixture();
        var operation = await f.Prepare("""{"name_by_user":"Új","area_id":"room","labels":["new"]}""");
        Assert.Equal(0, f.Writes);
        Assert.True(operation.RequiresLosslessAudit);
        Assert.Contains("Név: Régi → Új", operation.Presentation!.RegistryLine());
        Assert.Contains("Terület: Nincs → Szoba", operation.Presentation.RegistryLine());
        Assert.Contains("Címkék: Megmarad → Új címke", operation.Presentation.RegistryLine());
        Assert.Contains("\"name_by_user\":\"R", operation.Parameters["before"]);
        await f.Operation.ExecuteAsync(operation, default);
        Assert.Equal(1, f.Writes);
        Assert.Equal("config/device_registry/update", f.LastUpdate.GetProperty("type").GetString());
        Assert.Equal("device", f.LastUpdate.GetProperty("device_id").GetString());
        Assert.Equal("Új", f.LastUpdate.GetProperty("name_by_user").GetString());
        Assert.Equal(3, f.TemplateRequests.Count);
        Assert.All(f.TemplateRequests, template => Assert.Equal("{{ device_entities(\"device\") | to_json }}", template));
        Assert.DoesNotContain("config/entity_registry/list", f.Commands);
    }

    internal static Dictionary<string, JsonElement> Arguments(string patch) => new()
    {
        ["device_id"] = JsonSerializer.SerializeToElement("device"),
        ["patch"] = JsonSerializer.Deserialize<JsonElement>(patch)
    };

    // A HA Core 2026.11.0 helpers/device_registry.py DeviceEntry.dict_repr és
    // ChildDeviceEntry.dict_repr összes mezője; nincs éles rendszerkapcsolat.
    internal static JsonObject DeviceRecord(bool child = false) => JsonNode.Parse(child ? """
        {"area_id":null,"config_entry_id":"entry","config_subentry_id":null,"created_at":0,
         "disabled_by":null,"id":"device","identifiers":[["demo","child"]],"labels":["keep"],
         "modified_at":0,"name_by_user":"Régi","name":"Eredeti","next_name_part":null,"parent_device_id":"parent"}
        """ : """
        {"area_id":null,"configuration_url":null,"config_entries":["entry"],"config_entries_subentries":{"entry":[]},
         "config_entry_id":"entry","config_subentry_id":null,"connections":[],"created_at":0,"disabled_by":null,
         "entry_type":null,"hw_version":null,"id":"device","identifiers":[["demo","device"]],"labels":["keep"],
         "manufacturer":"Demo","model":"Model","model_id":null,"modified_at":0,"name_by_user":"Régi","name":"Eredeti",
         "next_name_part":null,"parent_device_id":null,"primary_config_entry":"entry","serial_number":null,"sw_version":null,"via_device_id":null}
        """)!.AsObject();

    internal sealed class Fixture : IDisposable
    {
        public JsonObject Device { get; set; } = DeviceRecord();
        public string ActiveResponse { get; set; } = "[\"sensor.room\",\"sensor.other\"]";
        public string Areas { get; set; } = "[{\"area_id\":\"room\",\"name\":\"Szoba\"}]";
        public string Labels { get; set; } = "[{\"label_id\":\"keep\",\"name\":\"Megmarad\"},{\"label_id\":\"new\",\"name\":\"Új címke\"}]";
        public bool TemplateFailure { get; set; }
        public bool UpdateFailure { get; set; }
        public Action? BeforeWrite { get; set; }
        public List<string> TemplateRequests { get; } = [];
        public List<string> Commands { get; } = [];
        public int Writes { get; private set; }
        public JsonElement LastUpdate { get; private set; }
        public HomeAssistantRegistryTests.MutableKillSwitch KillSwitch { get; } = new();
        public HomeAssistantReadToolsTests.RuntimeSecrets Secrets { get; } = new();
        public HomeAssistantLogTestSocketFactory Socket { get; }
        public HomeAssistantRegistryOperation Operation { get; }
        public HomeAssistantClient Client { get; }
        public RegistryPolicy Policy { get; }
        public Fixture(bool enabled = true, string[]? domains = null, string? globallyDenied = null)
        {
            Socket = new(() => throw new InvalidOperationException("Nincs parancs."), () => Secrets.Token)
            {
                RequestBody = command =>
                {
                    var type = command.GetProperty("type").GetString()!;
                    Commands.Add(type);
                    return type switch
                    {
                        "config/device_registry/list" => "[" + Device.ToJsonString() + "]",
                        "config/area_registry/list" => Areas,
                        "config/label_registry/list" => Labels,
                        "config/device_registry/update" => Update(command),
                        _ => throw new InvalidOperationException("Váratlan registry parancs.")
                    };
                }
            };
            Client = new(Options.Create(new HomeAssistantOptions { BaseUrl = "https://ha.example/", TokenSecretId = Guid.NewGuid().ToString(), DomainAllowlist = domains ?? ["sensor"] }), Secrets, new TemplateHandler(this), Socket);
            var values = new Dictionary<string, string?>
            {
                ["HomeAssistantRegistry:Enabled"] = enabled ? "true" : "false",
                ["HomeAssistantRegistry:EntityDenylist:0"] = "sensor.denied"
            };
            if (globallyDenied is not null) values["HomeAssistantControl:EntityDenylist:0"] = globallyDenied;
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            Policy = RegistryPolicy.FromConfiguration(config);
            Operation = new(new HomeAssistantRegistryService(Client, Policy, KillSwitch));
        }
        public ValueTask<PreparedOperation> Prepare(string patch) => Operation.PrepareAsync(Arguments(patch), default);
        private string Update(JsonElement command)
        {
            BeforeWrite?.Invoke();
            Writes++;
            LastUpdate = command.Clone();
            if (UpdateFailure) throw new HomeAssistantException("Az összetett eszköz nem módosítható.");
            return "{}";
        }
        public void Dispose() => Client.Dispose();
        private sealed class TemplateHandler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.Equal("/api/template", request.RequestUri!.AbsolutePath);
                Assert.Equal(HttpMethod.Post, request.Method);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                fixture.TemplateRequests.Add(body.RootElement.GetProperty("template").GetString()!);
                return new(fixture.TemplateFailure ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
                { Content = new StringContent(fixture.ActiveResponse) };
            }
        }
    }
}
