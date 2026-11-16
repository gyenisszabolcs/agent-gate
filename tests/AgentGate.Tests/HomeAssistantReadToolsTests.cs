using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Secrets;
using AgentGate.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantReadToolsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    public async Task ListSortsFiltersBeforeHardLimitAndProjectsOnlyMinimalFields(int count)
    {
        var rows = Enumerable.Range(0, count).Reverse().Select(index => new
        {
            entity_id = $"sensor.room_{index:D3}",
            state = "21",
            last_changed = "2026-11-14T10:20:30+00:00",
            attributes = new { friendly_name = "Szoba", unit_of_measurement = "°C", extra = new { coordinates = "47.123,19.456", nested = new[] { "tiltott" } } }
        }).Cast<object>().Prepend(new { entity_id = "light.desk", state = "on", attributes = new { } });
        using var fixture = new Fixture(JsonSerializer.Serialize(rows));
        var result = await Data(fixture.Tools.ListStates(CancellationToken.None, "sensor"));
        Assert.Equal(new[] { "items", "returned_count", "truncated" }, result.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(Math.Min(count, 100), result.GetProperty("returned_count").GetInt32());
        Assert.Equal(count > 100, result.GetProperty("truncated").GetBoolean());
        var items = result.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(Enumerable.Range(0, Math.Min(count, 100)).Select(index => $"sensor.room_{index:D3}"), items.Select(item => item.GetProperty("entity_id").GetString()));
        foreach (var item in items)
        {
            Assert.Equal(new[] { "entity_id", "friendly_name", "last_changed", "state", "unit_of_measurement" }, item.EnumerateObject().Select(property => property.Name).Order());
            Assert.Equal("2026-11-14T10:20:30+00:00", item.GetProperty("last_changed").GetString());
        }
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sensor")]
    [InlineData("sensor.room")]
    [InlineData("sensor/../config")]
    [InlineData("lock")]
    [InlineData("sensor\n")]
    public async Task InvalidOrDisallowedDomainNeverCallsProviderOrHttp(string domain)
    {
        using var fixture = new Fixture("[]");
        var error = await Assert.ThrowsAsync<GateToolException>(() => fixture.Tools.ListStates(CancellationToken.None, domain));
        Assert.Equal("Ez a domain nincs engedélyezve.", error.Message);
        Assert.Equal(0, fixture.Secrets.Calls);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task EmptyAllowlistNeverExpandsAndOptionalFieldsAreNotFabricated()
    {
        using var denied = new Fixture("[{\"entity_id\":\"sensor.room\",\"state\":\"on\",\"attributes\":{}}]", []);
        var empty = await Data(denied.Tools.ListStates(CancellationToken.None));
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
        await Assert.ThrowsAsync<GateToolException>(() => denied.Tools.ListStates(CancellationToken.None, "sensor"));
        using var allowed = new Fixture("[{\"entity_id\":\"sensor.room\",\"state\":\"on\",\"attributes\":{}}]");
        var single = (await Data(allowed.Tools.ListStates(CancellationToken.None))).GetProperty("items")[0];
        Assert.Equal(new[] { "entity_id", "state" }, single.EnumerateObject().Select(property => property.Name).Order());
    }

    [Theory]
    [InlineData("sensor")]
    [InlineData("person")]
    [InlineData("zone")]
    [InlineData("light")]
    public async Task DetailProjectsOnlyFiveStringAttributesInEveryDomain(string domain)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["friendly_name"] = "Nappali",
            ["unit_of_measurement"] = "°C",
            ["device_class"] = "temperature",
            ["state_class"] = "measurement",
            ["icon"] = "mdi:home",
            ["unknown"] = new { payload = new[] { new { latitude = "47.123,19.456" } } },
            ["safe_object"] = new { latitude = 47.123, longitude = 19.456 },
            ["safe_array"] = new[] { new { GPS_accuracy = 3 } },
            ["pre_GpsAccuracy_suffix"] = "47.123,19.456",
            ["gps-accuracy"] = "47.123,19.456",
            ["GPS_accuracy"] = "47.123,19.456",
            ["latitude"] = "47.123,19.456",
            ["longitude"] = "47.123,19.456",
            ["Coordinates"] = "47.123,19.456",
            ["LONGITUDE"] = "47.123,19.456",
            ["GpsAccuracy"] = "47.123,19.456",
            ["pre_Coordinates_suffix"] = "47.123,19.456",
            ["altitude"] = "47.123,19.456",
            ["elevation"] = "47.123,19.456",
            ["course"] = "47.123,19.456",
            ["speed"] = "47.123,19.456",
            ["radius"] = "47.123,19.456",
            ["wind_speed"] = "47.123,19.456"
        };
        var body = JsonSerializer.Serialize(new { entity_id = domain + ".room", state = "home", last_changed = "2026-11-14T10:20:30Z", attributes });
        using var fixture = new Fixture(body);
        var result = await Data(fixture.Tools.GetState(domain + ".room", CancellationToken.None));
        Assert.Equal(new[] { "attributes", "entity_id", "friendly_name", "last_changed", "state", "unit_of_measurement" }, result.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("home", result.GetProperty("state").GetString());
        Assert.Equal(new[] { "device_class", "friendly_name", "icon", "state_class", "unit_of_measurement" }, result.GetProperty("attributes").EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("mdi:home", result.GetProperty("attributes").GetProperty("icon").GetString());
        Assert.DoesNotContain("47.123", result.GetRawText());
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("lock.front")]
    [InlineData("sensor.bad/path")]
    [InlineData("sensor.x?token=bad")]
    [InlineData("sensor.x\n")]
    [InlineData("")]
    [InlineData(null)]
    public async Task InvalidEntityFailsBeforeAnyIo(string? id)
    {
        using var fixture = new Fixture("{}");
        var error = await Assert.ThrowsAsync<GateToolException>(() => fixture.Tools.GetState(id!, CancellationToken.None));
        Assert.Equal("Ez az entitás nincs engedélyezve.", error.Message);
        Assert.Equal(0, fixture.Secrets.Calls);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task ConfigProjectsExactlyFiveFieldsAndSevenStringUnits()
    {
        using var fixture = new Fixture("""
            {"version":"2026.11","time_zone":"Europe/Budapest","language":"hu","location_name":"Otthon",
             "unit_system":{"length":"km","mass":"kg","temperature":"°C","volume":"L","pressure":"hPa",
                "wind_speed":"km/h","accumulated_precipitation":"mm","latitude":"47.123","extra":{"longitude":19.456}},
             "latitude":47.123,"longitude":19.456,"elevation":100,"internal_url":"https://ha.example/",
             "external_url":"https://ha.example/","allowlist_external_dirs":["/secret"],"components":["sensor"],"extra":true}
            """);
        var result = await Data(fixture.Tools.GetConfig(CancellationToken.None));
        Assert.Equal(new[] { "language", "location_name", "time_zone", "unit_system", "version" }, result.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(new[] { "accumulated_precipitation", "length", "mass", "pressure", "temperature", "volume", "wind_speed" }, result.GetProperty("unit_system").EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("km/h", result.GetProperty("unit_system").GetProperty("wind_speed").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"language\":false,\"location_name\":{},\"time_zone\":[],\"unit_system\":null}")]
    [InlineData("{\"unit_system\":{\"length\":1,\"mass\":false,\"temperature\":{},\"volume\":[],\"pressure\":null}}")]
    public async Task ConfigNeverInventsMissingFieldsOrPassesCompositeValues(string body)
    {
        using var fixture = new Fixture(body);
        var result = await Data(fixture.Tools.GetConfig(CancellationToken.None));
        Assert.All(result.EnumerateObject(), property =>
        {
            Assert.Equal("unit_system", property.Name);
            Assert.Empty(property.Value.EnumerateObject());
        });
    }

    [Theory]
    [InlineData("person", "47.123, 19.456", "Nappali", "°C")]
    [InlineData("zone", "geo:47.123;19.456", "Nappali", "°C")]
    [InlineData("sensor", "latitude=47.123 longitude=19.456", "Nappali", "°C")]
    [InlineData("light", "47° 12′ 3″ N 19° 4′ 5″ E", "Nappali", "°C")]
    [InlineData("sensor", "21", "GPS pozíció", "°C")]
    [InlineData("sensor", "21", "Nappali", "degrees")]
    [InlineData("sensor", "21", "47.123 / 19.456", "°C")]
    [InlineData("sensor", "+47.123 -19.456", "Nappali", "°C")]
    [InlineData("person", "N 47.123 E 19.456", "Nappali", "°C")]
    [InlineData("zone", "47.123N 19.456E", "Nappali", "°C")]
    [InlineData("light", "lat=47.123 lon=19.456", "Nappali", "°C")]
    public async Task LocationLikeStatesAndDescriptorsAreOmittedFromListAndDeniedInDetail(string domain, string state, string name, string unit)
    {
        var row = new { entity_id = domain + ".room", state, attributes = new { friendly_name = name, unit_of_measurement = unit } };
        using var list = new Fixture(JsonSerializer.Serialize(new[] { row }));
        Assert.Empty((await Data(list.Tools.ListStates(CancellationToken.None))).GetProperty("items").EnumerateArray());
        using var detail = new Fixture(JsonSerializer.Serialize(row));
        var error = await Assert.ThrowsAsync<GateToolException>(() => detail.Tools.GetState(row.entity_id, CancellationToken.None));
        Assert.Equal("Az entitás helyadatgyanús adatot tartalmaz.", error.Message);
    }

    [Theory]
    [InlineData("sensor.gps_accuracy")]
    [InlineData("sensor.pre_latitude_suffix")]
    [InlineData("sensor.wind_speed")]
    [InlineData("person.coordinates")]
    [InlineData("zone.radius")]
    public async Task LocationNamedEntitiesAreNotReturned(string id)
    {
        var row = new { entity_id = id, state = "47.123", attributes = new { } };
        using var list = new Fixture(JsonSerializer.Serialize(new[] { row }));
        Assert.Empty((await Data(list.Tools.ListStates(CancellationToken.None))).GetProperty("items").EnumerateArray());
        using var detail = new Fixture(JsonSerializer.Serialize(row));
        await Assert.ThrowsAsync<GateToolException>(() => detail.Tools.GetState(id, CancellationToken.None));
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("http")]
    [InlineData("timeout")]
    public async Task ToolFailuresAreOwnConstantGateErrors(string failure)
    {
        using var fixture = new Fixture("{}", timeout: TimeSpan.FromMilliseconds(30));
        if (failure == "secret") fixture.Secrets.Read = _ => throw new InvalidOperationException(fixture.Secrets.Token);
        if (failure == "http") fixture.Handler.Respond = (_, _) => throw new HttpRequestException(fixture.Secrets.Token);
        if (failure == "timeout") fixture.Secrets.Read = async ct => { await Task.Delay(Timeout.Infinite, ct); return fixture.Secrets.Token; };
        var error = await Assert.ThrowsAsync<GateToolException>(() => fixture.Tools.GetConfig(CancellationToken.None));
        Assert.Equal("A Home Assistant lekérés sikertelen.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("location_name")]
    [InlineData("language")]
    [InlineData("version")]
    [InlineData("time_zone")]
    [InlineData("length")]
    public async Task ConfigOmitsLocationTextEvenInAllowedFields(string field)
    {
        var data = field == "length" ? new { unit_system = new Dictionary<string, string> { [field] = "47.123,19.456" } } as object :
            new Dictionary<string, string> { [field] = "47.123,19.456" };
        using var fixture = new Fixture(JsonSerializer.Serialize(data));
        var result = await Data(fixture.Tools.GetConfig(CancellationToken.None));
        Assert.False(result.ToString().Contains("47.123", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("friendly_name", "{\"coordinates\":\"47.123,19.456\"}")]
    [InlineData("unit_of_measurement", "[\"47.123,19.456\"]")]
    [InlineData("device_class", "47.123")]
    [InlineData("state_class", "true")]
    [InlineData("icon", "null")]
    public async Task AllowedAttributeNamesNeverAuthorizeCompositeOrNonStringValues(string field, string json)
    {
        var value = JsonSerializer.Deserialize<JsonElement>(json);
        using var fixture = new Fixture(JsonSerializer.Serialize(new { entity_id = "sensor.room", state = "21", attributes = new Dictionary<string, object?> { [field] = value } }));
        var result = await Data(fixture.Tools.GetState("sensor.room", CancellationToken.None));
        Assert.Empty(result.GetProperty("attributes").EnumerateObject());
        Assert.False(result.TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData("47.123")]
    [InlineData("tomorrow")]
    [InlineData("2026-11-14")]
    [InlineData("2026-11-14T10:20:30Z GPS")]
    public async Task InvalidLastChangedIsNotInventedOrEchoed(string timestamp)
    {
        using var fixture = new Fixture(JsonSerializer.Serialize(new { entity_id = "sensor.room", state = "21", last_changed = timestamp, attributes = new { } }));
        var result = await Data(fixture.Tools.GetState("sensor.room", CancellationToken.None));
        Assert.False(result.TryGetProperty("last_changed", out _));
    }

    [Theory]
    [InlineData("sensor", "friendly_name", "G-P_S accuracy")]
    [InlineData("person", "unit_of_measurement", "  DeGrEeS  ")]
    [InlineData("zone", "device_class", "pre_GpsAccuracy_suffix")]
    [InlineData("light", "state_class", "coordinates")]
    [InlineData("sensor", "icon", "geo:47.123,19.456")]
    public async Task EveryAllowedTextFieldParticipatesInLocationDecision(string domain, string field, string value)
    {
        var row = new { entity_id = domain + ".room", state = "home", attributes = new Dictionary<string, string> { [field] = value } };
        using var list = new Fixture(JsonSerializer.Serialize(new[] { row }));
        Assert.Empty((await Data(list.Tools.ListStates(CancellationToken.None))).GetProperty("items").EnumerateArray());
        using var detail = new Fixture(JsonSerializer.Serialize(row));
        await Assert.ThrowsAsync<GateToolException>(() => detail.Tools.GetState(row.entity_id, CancellationToken.None));
    }

    [Theory]
    [InlineData("list", true)]
    [InlineData("state", true)]
    [InlineData("config", true)]
    [InlineData("list", false)]
    [InlineData("state", false)]
    [InlineData("config", false)]
    public async Task CallerCancellationReachesProviderOrHttpWithoutEcho(string tool, bool duringSecret)
    {
        using var fixture = new Fixture("{}");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Block(CancellationToken ct)
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw new OperationCanceledException(fixture.Secrets.Token); }
            return fixture.Secrets.Token;
        }
        if (duringSecret) fixture.Secrets.Read = Block;
        else fixture.Handler.Respond = async (_, ct) => { await Block(ct); return new HttpResponseMessage(HttpStatusCode.OK); };
        using var cancellation = new CancellationTokenSource();
        var pending = tool switch
        {
            "list" => fixture.Tools.ListStates(cancellation.Token),
            "state" => fixture.Tools.GetState("sensor.room", cancellation.Token),
            _ => fixture.Tools.GetConfig(cancellation.Token)
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(error.ToString().Contains(fixture.Secrets.Token, StringComparison.Ordinal), "Titok a megszakításban.");
        Assert.Equal(duringSecret ? 0 : 1, fixture.Handler.Calls);
    }

    private static async Task<JsonElement> Data(Task<ModelContextProtocol.Protocol.CallToolResult> pending) =>
        (await pending).StructuredContent!.Value;

    [Fact]
    public void ExampleContainsExactlyApprovedTwelveDomainsInOrder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentGate.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "src/AgentGate/Integrations/HomeAssistant/appsettings.example.jsonc");
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        Assert.Equal(new[] { "light", "switch", "sensor", "binary_sensor", "climate", "cover", "weather", "sun", "scene", "input_boolean", "person", "zone" },
            document.RootElement.GetProperty("HomeAssistant").GetProperty("DomainAllowlist").EnumerateArray().Select(value => value.GetString()));
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider services;
        public Handler Handler { get; }
        public RuntimeSecrets Secrets { get; } = new();
        public HomeAssistantTools Tools { get; }
        public Fixture(string body, string[]? domains = null, TimeSpan? timeout = null)
        {
            Handler = new Handler(body);
            var options = Options.Create(new HomeAssistantOptions
            {
                BaseUrl = "https://ha.example/",
                TokenSecretId = Guid.NewGuid().ToString(),
                DomainAllowlist = domains ?? ["sensor", "light", "person", "zone"],
                Timeout = timeout ?? TimeSpan.FromSeconds(2)
            });
            services = new ServiceCollection().AddSingleton(_ => new HomeAssistantClient(options, Secrets, Handler)).BuildServiceProvider();
            Tools = new HomeAssistantTools(services);
        }
        public void Dispose() => services.Dispose();
    }

    internal sealed class RuntimeSecrets : ISecretProvider
    {
        public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public Func<CancellationToken, Task<string>>? Read { get; set; }
        public int Calls { get; private set; }
        public Task<string> GetSecretAsync(Guid secretId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Read?.Invoke(cancellationToken) ?? Task.FromResult(Token);
        }
    }

    internal sealed class Handler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Respond?.Invoke(request, cancellationToken) ?? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
