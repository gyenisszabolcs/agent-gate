using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Integrations.HomeAssistant.Registry;
using AgentGate.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantRegistryTests
{
    [Fact]
    public async Task PreparationBindsOldValuesAndPreservesUnchangedLabels()
    {
        using var f = new Fixture();
        var op = await f.Operation.PrepareAsync(Arguments("{\"labels_add\":[\"new\"],\"name\":\"Új\"}"), default);
        Assert.Equal(RiskLevel.Configuration, op.Risk);
        using var before = JsonDocument.Parse(op.Parameters["before"]!);
        Assert.Equal("Régi", before.RootElement.GetProperty("name").GetString());
        Assert.Contains("keep", op.Parameters["after"]!);
        Assert.Contains("new", op.Parameters["after"]!);
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("{\"new_entity_id\":\"sensor.other\"}")]
    [InlineData("{\"name\":\"a\",\"name\":\"b\"}")]
    [InlineData("{\"disabled_by\":\"integration\"}")]
    [InlineData("{\"labels\":[\"new\"]}")]
    [InlineData("{\"labels_add\":[\"missing\"]}")]
    [InlineData("{\"area_id\":\"missing\"}")]
    [InlineData("{\"labels_add\":[\"new\"],\"labels_remove\":[\"new\"]}")]
    [InlineData("{\"labels_add\":[\"new\",\"new\"]}")]
    [InlineData("{\"hidden_by\":false}")]
    [InlineData("{\"name\":\"password=private\"}")]
    [InlineData("{}")]
    public async Task InvalidIntentNeverWrites(string patch)
    {
        using var f = new Fixture();
        await Assert.ThrowsAnyAsync<Exception>(async () => await f.Operation.PrepareAsync(Arguments(patch), default));
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("lock.door")]
    [InlineData("sensor.denied")]
    [InlineData("Sensor.room")]
    public async Task DisallowedTargetNeverConnects(string id)
    {
        using var f = new Fixture();
        await Assert.ThrowsAnyAsync<Exception>(async () => await f.Operation.PrepareAsync(Arguments("{\"name\":\"Új\"}", id), default));
        Assert.Equal(0, f.Socket.Calls);
    }

    [Fact]
    public async Task ChangedPrestateRejectsWithoutWrite()
    {
        using var f = new Fixture();
        var op = await f.Operation.PrepareAsync(Arguments("{\"name\":\"Új\"}"), default);
        f.Name = "Más";
        await Assert.ThrowsAnyAsync<Exception>(() => f.Operation.ExecuteAsync(op, default));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task IntegrationOwnedDisableCannotBeCleared()
    {
        using var f = new Fixture { DisabledBy = "integration" };
        await Assert.ThrowsAnyAsync<Exception>(async () => await f.Operation.PrepareAsync(Arguments("{\"disabled_by\":null}"), default));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task NullIsAnExplicitResetAndMissingFieldIsNotSent()
    {
        using var f = new Fixture();
        var op = await f.Operation.PrepareAsync(Arguments("{\"name\":null}"), default);
        await f.Operation.ExecuteAsync(op, default);
        var sent = f.LastUpdate;
        Assert.Equal("config/entity_registry/update", sent.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("name").ValueKind);
        Assert.False(sent.TryGetProperty("area_id", out _));
        Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData("{\"name_by_user\":\"Új\"}")]
    [InlineData("{\"area_id\":\"room\"}")]
    [InlineData("{\"labels\":[\"new\"]}")]
    [InlineData("{\"name\":\"Új\"}")]
    public async Task AmbiguousEntityAndDeviceTargetsAreRejectedBeforeIo(string patch)
    {
        using var f = new Fixture();
        var args = Arguments(patch);

        args["device_id"] = JsonSerializer.SerializeToElement("1234567890abcdef1234567890abcdef");
        await Assert.ThrowsAnyAsync<Exception>(async () => await f.Operation.PrepareAsync(args, default));
        Assert.Equal(0, f.Socket.Calls);
        Assert.Equal(0, f.Writes);
    }

    internal static Dictionary<string, JsonElement> Arguments(string patch, string id = "sensor.room") => new()
    {
        ["entity_id"] = JsonSerializer.SerializeToElement(id),
        ["patch"] = JsonDocument.Parse(patch).RootElement.Clone()
    };

    internal sealed class Fixture : IDisposable
    {
        public string? Name { get; set; } = "Régi";
        public string? OriginalName { get; set; }
        public string? DeviceName { get; set; }
        public string? DeviceNameByUser { get; set; }

        public string? Area { get; set; }
        public string? Device { get; set; }
        public string? DeviceArea { get; set; } = "bedroom";
        public string? DevicesCatalog { get; set; }
        public int DeviceReads { get; private set; }
        public string LabelsCatalog { get; set; } = "[{\"label_id\":\"keep\",\"name\":\"Megmarad\"},{\"label_id\":\"new\",\"name\":\"Új\"}]";
        public string AreasCatalog { get; set; } = "[{\"area_id\":\"room\",\"name\":\"Szoba\"}]";
        public string? DisabledBy { get; set; }
        public int Writes { get; private set; }
        public JsonElement LastUpdate { get; private set; }
        public Action? BeforeWrite { get; set; }
        public HomeAssistantLogTestSocketFactory Socket { get; }
        public HomeAssistantReadToolsTests.Handler Http { get; }
        public HomeAssistantClient Client { get; }
        public HomeAssistantRegistryOperation Operation { get; }
        public RegistryPolicy Policy { get; }
        public MutableKillSwitch KillSwitch { get; } = new();
        public HomeAssistantReadToolsTests.RuntimeSecrets Secrets { get; } = new();
        public Fixture(string body = "{}")
        {
            Socket = new(() => throw new InvalidOperationException("Nincs registry parancs."), () => Secrets.Token)
            {
                RequestBody = command => command.GetProperty("type").GetString() switch
                {
                    "config/entity_registry/get" => JsonSerializer.Serialize(new { entity_id = "sensor.room", device_id = Device, area_id = Area, labels = new[] { "keep" }, name = Name, original_name = OriginalName, icon = (string?)null, hidden_by = (string?)null, disabled_by = DisabledBy }),
                    "config/device_registry/list" => Devices(),
                    "config/label_registry/list" => LabelsCatalog,
                    "config/area_registry/list" => AreasCatalog,
                    "config/entity_registry/update" => Updated(command),
                    _ => throw new InvalidOperationException("Váratlan websocket parancs.")
                }
            };
            Http = new(body);
            Client = new(Options.Create(new HomeAssistantOptions { BaseUrl = "https://ha.example/", TokenSecretId = Guid.NewGuid().ToString(), DomainAllowlist = ["sensor"] }), Secrets, Http, Socket);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HomeAssistantRegistry:Enabled"] = "true",
                ["HomeAssistantRegistry:IncludeEntityLabels"] = "true",
                ["HomeAssistantRegistry:EntityDenylist:0"] = "sensor.denied"
            }).Build();
            Policy = RegistryPolicy.FromConfiguration(config);
            Operation = new(new HomeAssistantRegistryService(Client, Policy, KillSwitch));
        }
        private string Updated(JsonElement command) { BeforeWrite?.Invoke(); LastUpdate = command.Clone(); Writes++; return "{}"; }
        private string Devices() { DeviceReads++; return DevicesCatalog ?? JsonSerializer.Serialize(new[] { new { id = "device", area_id = DeviceArea, name = DeviceName, name_by_user = DeviceNameByUser } }); }
        public void Dispose() => Client.Dispose();

    }

    internal sealed class MutableKillSwitch : IKillSwitchStateSource
    {
        public bool Enabled { get; set; }
        public ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Enabled);
        }
    }
}
