using System.Net;
using System.Text.Json;
using AgentGate.Tools;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantDeviceReadTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("light")]
    [InlineData("sensor")]
    public async Task ExactDeviceFilterCombinesWithDomainAndArea(string? domain)
    {
        using var fixture = Fixture(false, Metadata);
        var method = typeof(HomeAssistantTools).GetMethod(nameof(HomeAssistantTools.ListStates))!;
        Assert.Contains(method.GetParameters(), parameter => parameter.Name == "device_id" && parameter.HasDefaultValue);
        var response = await (Task<ModelContextProtocol.Protocol.CallToolResult>)method.Invoke(fixture.Tools,
            new object?[] { default(CancellationToken), domain, "Dolgozó", "yeelight_reka" })!;
        var items = response.StructuredContent!.Value.GetProperty("items");
        Assert.Equal(domain == "sensor" ? 0 : 1, items.GetArrayLength());
    }

    [Theory]
    [InlineData(false, "[]")]
    [InlineData(true, "[]")]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    [InlineData(false, "[{\"entity_id\":\"light.reka\"}]")]
    [InlineData(true, "[{\"entity_id\":\"light.reka\"}]")]
    public async Task MissingRelationshipNeverMeansNoDevice(bool single, string metadata)
    {
        using var fixture = Fixture(single, metadata);
        await Assert.ThrowsAsync<GateToolException>(() => Call(fixture, single));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNullDeviceAndAreaArePreserved(bool single)
    {
        foreach (var device in new[] { "null", "{\"device_id\":\"yeelight_reka\",\"name\":\"Yeelight\",\"area\":null}" })
        {
            using var fixture = Fixture(single, "[{\"entity_id\":\"light.reka\",\"device\":" + device + "}]");
            var response = await Call(fixture, single);
            var data = response.StructuredContent!.Value;
            var actual = (single ? data : data.GetProperty("items")[0]).GetProperty("device");
            Assert.Equal(device == "null" ? JsonValueKind.Null : JsonValueKind.Object, actual.ValueKind);
            if (actual.ValueKind == JsonValueKind.Object) Assert.Equal(JsonValueKind.Null, actual.GetProperty("area").ValueKind);
        }
    }

    [Theory]
    [InlineData("wrong relationship")]
    [InlineData("duplicate relationship")]
    [InlineData("unknown field")]
    [InlineData("missing area")]
    [InlineData("empty area name")]
    [InlineData("location name")]
    [InlineData("long name")]
    [InlineData("control name")]
    [InlineData("bad id")]
    [InlineData("duplicate field")]
    public async Task MalformedCatalogFailsClosed(string fault)
    {
        var metadata = fault switch
        {
            "wrong relationship" => Metadata.Replace("light.reka", "light.other", StringComparison.Ordinal),
            "duplicate relationship" => "[" + Metadata[1..^1] + "," + Metadata[1..^1] + "]",
            "unknown field" => Metadata.Replace("\"name\":", "\"unexpected\":\"hidden\",\"name\":", StringComparison.Ordinal),
            "missing area" => Metadata.Replace(",\"area\":{\"area_id\":\"reka\",\"area_name\":\"Réka szobája\"}", "", StringComparison.Ordinal),
            "empty area name" => Metadata.Replace("Réka szobája", "", StringComparison.Ordinal),
            "location name" => Metadata.Replace("Yeelight Réka", "47.123,19.456", StringComparison.Ordinal),
            "long name" => Metadata.Replace("Yeelight Réka", new string('x', 257), StringComparison.Ordinal),
            "control name" => Metadata.Replace("Yeelight Réka", "Yeelight\\u0001Réka", StringComparison.Ordinal),
            "bad id" => Metadata.Replace("yeelight_reka", "bad/id", StringComparison.Ordinal),
            _ => Metadata.Replace("\"device_id\":", "\"device_id\":\"other\",\"device_id\":", StringComparison.Ordinal)
        };
        using var fixture = Fixture(false, metadata);
        await Assert.ThrowsAsync<GateToolException>(() => Call(fixture, false));
    }

    private static Task<ModelContextProtocol.Protocol.CallToolResult> Call(HomeAssistantReadToolsTests.Fixture fixture, bool single) =>
        single ? fixture.Tools.GetState("light.reka", default) : fixture.Tools.ListStates(default);

    internal static HomeAssistantReadToolsTests.Fixture Fixture(bool single, string metadata)
    {
        var fixture = new HomeAssistantReadToolsTests.Fixture(single ? State : "[" + State + "]");
        fixture.Handler.Respond = async (request, ct) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath == "/api/template"
                ? (await request.Content!.ReadAsStringAsync(ct)).Contains("device_attr", StringComparison.Ordinal) ? metadata : Areas
                : single ? State : "[" + State + "]")
        };
        return fixture;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateToolsExposeYeelightDeviceSeparateFromEntityArea(bool single)
    {
        using var fixture = new HomeAssistantReadToolsTests.Fixture(single ? State : "[" + State + "]");
        fixture.Handler.Respond = async (request, ct) =>
        {
            string? template = null;
            if (request.RequestUri!.AbsolutePath == "/api/template")
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                template = payload.RootElement.GetProperty("template").GetString();
            }
            var devices = template?.Contains("device_attr", StringComparison.Ordinal) == true;
            if (devices)
            {
                // A fix HA-kérés szerződése: felhasználói név elsőbbsége, registry-név fallback, saját terület és visszakapcsolat.
                Assert.Contains("device_attr(id, 'name_by_user') or device_attr(id, 'name')", template);
                Assert.Contains("own_area = device_attr(id, 'area_id')", template);
                Assert.Contains("entity not in device_entities(id)", template);
            }
            var body = template is not null ? devices ? Metadata : Areas : single ? State : "[" + State + "]";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        };
        var response = single ? await fixture.Tools.GetState("light.reka", default) : await fixture.Tools.ListStates(default);
        var data = response.StructuredContent!.Value;
        var state = single ? data : Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal("Dolgozó", state.GetProperty("area_name").GetString());
        var device = state.GetProperty("device");
        Assert.Equal("yeelight_reka", device.GetProperty("device_id").GetString());
        Assert.Equal("Yeelight Réka", device.GetProperty("name").GetString());
        Assert.Equal("reka", device.GetProperty("area").GetProperty("area_id").GetString());
        Assert.Equal("Réka szobája", device.GetProperty("area").GetProperty("area_name").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeviceAndOwnAreaNamesUseExistingSecretRedaction(bool area)
    {
        // A véletlen hexszöveg önmagában irányjelként (pl. E1) ismerhető fel; ez itt titokszűrési, nem helyadatteszt.
        var canary = "private" + Guid.NewGuid().ToString("N") + "private";
        var metadata = Metadata.Replace(area ? "Réka szobája" : "Yeelight Réka", "password=" + canary, StringComparison.Ordinal);
        using var fixture = Fixture(false, metadata);
        var result = await Call(fixture, false);
        Assert.False(result.StructuredContent!.Value.GetRawText().Contains(canary, StringComparison.Ordinal), "Titok az eszköz-metaadatban.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Yeelight Réka")]
    [InlineData("bad/id")]
    [InlineData("DEVICE")]
    [InlineData("gps_device")]
    public async Task InvalidDeviceFilterIsRejectedBeforeIo(string filter)
    {
        using var fixture = Fixture(false, Metadata);
        await Assert.ThrowsAsync<GateToolException>(() => fixture.Tools.ListStates(default, device_id: filter));
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Equal(0, fixture.Secrets.Calls);
    }

    [Fact]
    public async Task DeviceFilterRunsBeforeTheHundredStateLimit()
    {
        using var fixture = Fixture(false, Metadata);
        var normal = fixture.Handler.Respond!;
        fixture.Handler.Respond = async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/states")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + string.Join(',', Enumerable.Range(0, 110).Select(i => "{\"entity_id\":\"light.a" + i + "\",\"state\":\"on\",\"attributes\":{}}").Append(State)) + "]") };
            if (request.RequestUri.AbsolutePath == "/api/template")
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                if (body.Contains("device_attr", StringComparison.Ordinal))
                {
                    using var rows = JsonDocument.Parse(HomeAssistantReadToolsTests.Handler.TemplatePayload(body));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + string.Join(',', rows.RootElement.EnumerateArray().Select(row => row.GetProperty("entity_id").GetString() == "light.reka" ? Metadata[1..^1] : row.GetRawText())) + "]") };
                }
            }
            return await normal(request, ct);
        };
        var result = (await fixture.Tools.ListStates(default, device_id: "yeelight_reka")).StructuredContent!.Value;
        Assert.Equal("light.reka", Assert.Single(result.GetProperty("items").EnumerateArray()).GetProperty("entity_id").GetString());
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    internal const string State = """{"entity_id":"light.reka","state":"on","attributes":{"friendly_name":"Réka lámpája"}}""";
    internal const string Areas = """[{"area_id":"office","area_name":"Dolgozó","entities":["light.reka"]}]""";
    internal const string Metadata = """[{"entity_id":"light.reka","device":{"device_id":"yeelight_reka","name":"Yeelight Réka","area":{"area_id":"reka","area_name":"Réka szobája"}}}]""";
}
