using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentGate.Tools;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantMediaReadTests
{
    private static readonly string[] MediaNames =
        ["is_volume_muted", "media_artist", "media_content_type", "media_title", "volume_level"];

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 1, true)]
    [InlineData(false, 0.35, true)]
    public async Task ListAndDetailExposeOnlyTypedMediaAttributes(bool detail, double volume, bool muted)
    {
        using var fixture = CreateFixture(JsonSerializer.Serialize(new
        {
            friendly_name = "Hangszóró", volume_level = volume, is_volume_muted = muted,
            media_title = "Dal", media_artist = "Előadó", media_content_type = "music",
            media_content_id = "https://example.invalid/song", entity_picture = "/private.jpg",
            source_list = new[] { "Rejtett" }, arbitrary = new { nested = "Rejtett" }
        }));
        var state = await State(fixture, detail);
        Assert.Equal("playing", state.GetProperty("state").GetString());
        var attributes = state.GetProperty("attributes");
        Assert.Equal(MediaNames.Prepend("friendly_name").Order(), attributes.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(volume, attributes.GetProperty("volume_level").GetDouble());
        Assert.Equal(muted, attributes.GetProperty("is_volume_muted").GetBoolean());
        Assert.Equal("Dal", attributes.GetProperty("media_title").GetString());
        Assert.Equal("Előadó", attributes.GetProperty("media_artist").GetString());
        Assert.Equal("music", attributes.GetProperty("media_content_type").GetString());
        Assert.All(MediaNames, name => Assert.False(state.TryGetProperty(name, out _)));
    }

    [Theory]
    [InlineData("1.00000000000000000000001")]
    [InlineData("-1e-1000")]
    public async Task VolumeOutsideExactRangeIsOmittedRatherThanRounded(string volume)
    {
        using var fixture = CreateFixture("{\"volume_level\":" + volume + "}");
        Assert.False((await State(fixture, true)).GetProperty("attributes").TryGetProperty("volume_level", out _));
    }

    [Theory]
    [InlineData("media_title")]
    [InlineData("media_artist")]
    [InlineData("media_content_type")]
    public async Task MediaTextUsesExistingSecretRedactor(string field)
    {
        // A hexadecimális E számjegy mellett égtájgyanús lehet; itt csak a titokszűrést vizsgáljuk.
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).Replace('E', 'X');
        using var fixture = CreateFixture(JsonSerializer.Serialize(new Dictionary<string, string> { [field] = "password=" + canary }));
        foreach (var detail in new[] { false, true })
        {
            var state = await State(fixture, detail);
            Assert.False(state.GetRawText().Contains(canary, StringComparison.Ordinal), "Titok a médiaválaszban.");
            Assert.Equal("password=[REDACTED]", state.GetProperty("attributes").GetProperty(field).GetString());
        }
    }

    [Fact]
    public async Task LocationLikeSecretIsOmittedBeforeMediaTextRedaction()
    {
        // Az eredeti véletlen hexadecimális bemenet is végződhetett számjegy + E alakra.
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24))[..^2] + "1E";
        using var fixture = CreateFixture(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["media_title"] = "password=" + canary, ["volume_level"] = 0.25
        }));
        foreach (var detail in new[] { false, true })
        {
            var state = await State(fixture, detail);
            Assert.False(state.GetRawText().Contains(canary, StringComparison.Ordinal), "Titok a médiaválaszban.");
            var attributes = state.GetProperty("attributes");
            Assert.False(attributes.TryGetProperty("media_title", out _));
            Assert.Equal(0.25, attributes.GetProperty("volume_level").GetDouble());
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"volume_level\":\"0.5\",\"is_volume_muted\":\"true\",\"media_title\":1,\"media_artist\":false,\"media_content_type\":null}")]
    [InlineData("{\"volume_level\":null,\"is_volume_muted\":0,\"media_title\":{},\"media_artist\":[],\"media_content_type\":true}")]
    [InlineData("{\"volume_level\":-0.01,\"is_volume_muted\":null}")]
    [InlineData("{\"volume_level\":1.01,\"is_volume_muted\":{}}")]
    [InlineData("{\"volume_level\":1e400,\"is_volume_muted\":[]}")]
    public async Task MissingOrInvalidValuesAreNeverInvented(string attributes)
    {
        using var fixture = CreateFixture(attributes);
        foreach (var detail in new[] { false, true })
            Assert.Empty((await State(fixture, detail)).GetProperty("attributes").EnumerateObject());
    }

    [Theory]
    [InlineData("media_title")]
    [InlineData("media_artist")]
    [InlineData("media_content_type")]
    public async Task BearerCanaryAndUnsafeUrlsRemainSanitized(string field)
    {
        using var fixture = CreateFixture("{}");
        fixture.Handler.Respond = (request, _) =>
        {
            var attributes = new Dictionary<string, string> { [field] = fixture.Secrets.Token, ["media_content_id"] = "https://example.invalid/private" };
            var row = new { entity_id = "media_player.room", state = "playing", attributes };
            object payload = request.RequestUri!.AbsolutePath switch
            {
                "/api/template" => Array.Empty<object>(),
                "/api/states" => new[] { row },
                _ => row
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(payload) });
        };
        foreach (var detail in new[] { false, true })
        {
            var state = await State(fixture, detail);
            Assert.False(state.GetRawText().Contains(fixture.Secrets.Token, StringComparison.Ordinal), "Bearer titok a médiaválaszban.");
            Assert.Equal("[SZŰRVE]", state.GetProperty("attributes").GetProperty(field).GetString());
        }
        using var url = CreateFixture(JsonSerializer.Serialize(new Dictionary<string, string> { [field] = "https://example.invalid/song?token=fictive" }));
        Assert.Equal("[SZŰRVE]", (await State(url, true)).GetProperty("attributes").GetProperty(field).GetString());
    }

    [Theory]
    [InlineData("media_title")]
    [InlineData("media_artist")]
    [InlineData("media_content_type")]
    public async Task LocationLikeMediaTextIsNotReleased(string field)
    {
        using var fixture = CreateFixture(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [field] = "47.123,19.456", ["volume_level"] = 0.25
        }));
        foreach (var detail in new[] { false, true })
        {
            var attributes = (await State(fixture, detail)).GetProperty("attributes");
            Assert.False(attributes.TryGetProperty(field, out _));
            Assert.Equal(0.25, attributes.GetProperty("volume_level").GetDouble());
        }
    }

    [Theory]
    [InlineData("sensor.room")]
    [InlineData("media_player_extra.room")]
    public async Task OtherDomainsDoNotGainMediaOutput(string entity)
    {
        using var fixture = CreateFixture("{\"volume_level\":0.5,\"is_volume_muted\":true,\"media_title\":\"Dal\",\"media_artist\":\"Előadó\",\"media_content_type\":\"music\"}", entity,
            [entity.Split('.')[0]]);
        Assert.False((await State(fixture, false, entity)).TryGetProperty("attributes", out _));
        Assert.Empty((await State(fixture, true, entity)).GetProperty("attributes").EnumerateObject());
    }

    [Fact]
    public async Task ReadAllowlistStillDeniesMediaBeforeIoAndFiltersList()
    {
        using var fixture = CreateFixture("{\"volume_level\":0.5}", domains: ["sensor"]);
        await Assert.ThrowsAsync<GateToolException>(() => fixture.Tools.GetState("media_player.room", CancellationToken.None));
        await Assert.ThrowsAsync<GateToolException>(() => fixture.Tools.ListStates(CancellationToken.None, "media_player"));
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Equal(0, fixture.Secrets.Calls);
        var list = (await fixture.Tools.ListStates(CancellationToken.None)).StructuredContent!.Value;
        Assert.Empty(list.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ExistingLocationAndAreaFiltersStillApplyToMedia()
    {
        using var unsafeState = CreateFixture("{\"friendly_name\":\"GPS pozíció\",\"volume_level\":0.5}");
        Assert.Empty((await unsafeState.Tools.ListStates(CancellationToken.None)).StructuredContent!.Value.GetProperty("items").EnumerateArray());
        await Assert.ThrowsAsync<GateToolException>(() => unsafeState.Tools.GetState("media_player.room", CancellationToken.None));
        using var fixture = CreateFixture("{\"volume_level\":0.5}");
        var respond = fixture.Handler.Respond!;
        fixture.Handler.Respond = (request, ct) => request.RequestUri!.AbsolutePath == "/api/template"
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"area_id\":\"living\",\"area_name\":\"Nappali\",\"entities\":[\"media_player.room\"]}]") })
            : respond(request, ct);
        var list = (await fixture.Tools.ListStates(CancellationToken.None, "media_player", "living")).StructuredContent!.Value;
        Assert.Equal(0.5, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("attributes").GetProperty("volume_level").GetDouble());
        Assert.Empty((await fixture.Tools.ListStates(CancellationToken.None, "media_player", "other")).StructuredContent!.Value.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MediaCannotBypassTransportSizeOrDepthBounds(bool deep)
    {
        var payload = deep ? new string('[', 40) + "0" + new string(']', 40) : JsonSerializer.Serialize(new string('a', 1024 * 1024));
        using var fixture = CreateFixture("{\"media_title\":" + payload + "}");
        foreach (var detail in new[] { false, true })
            await Assert.ThrowsAsync<GateToolException>(() => State(fixture, detail));
    }

    [Fact]
    public async Task NumericTokenBoundIsAppliedWithoutChangingValidRepresentation()
    {
        using var valid = CreateFixture("{\"volume_level\":0.2500}");
        Assert.Equal("0.2500", (await State(valid, true)).GetProperty("attributes").GetProperty("volume_level").GetRawText());
        using var oversized = CreateFixture("{\"volume_level\":1e-" + new string('9', 4096) + "}");
        Assert.Empty((await State(oversized, true)).GetProperty("attributes").EnumerateObject());
    }

    private static HomeAssistantReadToolsTests.Fixture CreateFixture(string attributes, string entity = "media_player.room", string[]? domains = null)
    {
        var fixture = new HomeAssistantReadToolsTests.Fixture("{}", domains ?? ["media_player", "sensor"]);
        fixture.Handler.Respond = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath == "/api/template" ? new StringContent("[]") :
                new StringContent(request.RequestUri.AbsolutePath == "/api/states" ? $"[{Row()}]" : Row())
        });
        return fixture;

        string Row() => JsonSerializer.Serialize(new { entity_id = entity, state = "playing", attributes = JsonSerializer.Deserialize<JsonElement>(attributes) });
    }

    private static async Task<JsonElement> State(HomeAssistantReadToolsTests.Fixture fixture, bool detail, string entity = "media_player.room")
    {
        var result = detail ? await fixture.Tools.GetState(entity, CancellationToken.None) : await fixture.Tools.ListStates(CancellationToken.None);
        var data = result.StructuredContent!.Value;
        Assert.Equal(data.GetRawText(), Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(result.Content)).Text);
        return detail ? data : Assert.Single(data.GetProperty("items").EnumerateArray());
    }
}
