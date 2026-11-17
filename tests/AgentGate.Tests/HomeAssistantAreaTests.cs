using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Tools;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantAreaTests
{
    [Fact]
    public async Task DetailIncludesAreaNameFromFixedTemplate()
    {
        using var fixture = CreateFixture();
        var result = (await fixture.Tools.GetState("sensor.room", CancellationToken.None)).StructuredContent!.Value;
        Assert.Equal("Nappali", result.GetProperty("area_name").GetString());
    }

    [Fact]
    public async Task ListIncludesAreaNamesAndExplicitNullForUnassignedEntity()
    {
        using var fixture = CreateFixture();
        var result = (await fixture.Tools.ListStates(CancellationToken.None)).StructuredContent!.Value;
        var items = result.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("Nappali", items.Single(item => item.GetProperty("entity_id").GetString() == "sensor.room").GetProperty("area_name").GetString());
        Assert.Equal(JsonValueKind.Null, items.Single(item => item.GetProperty("entity_id").GetString() == "sensor.outside").GetProperty("area_name").ValueKind);
    }

    [Theory]
    [InlineData("area_name", "47.123,19.456")]
    [InlineData("area_id", "gps_position")]
    public async Task LocationLikeAreaDescriptorsNeverBypassReadProjection(string field, string value)
    {
        var row = new Dictionary<string, object> { ["area_id"] = "living", ["area_name"] = "Nappali", ["entities"] = new[] { "sensor.room" } };
        row[field] = value;
        using var fixture = CreateFixture(JsonSerializer.Serialize(new[] { row }));
        var detail = (await fixture.Tools.GetState("sensor.room", CancellationToken.None)).StructuredContent!.Value;
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("area_name").ValueKind);
        var areas = (await fixture.Tools.ListAreas(CancellationToken.None)).StructuredContent!.Value;
        Assert.Empty(areas.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("Nappali\n")]
    public async Task InvalidAreaFilterFailsBeforeAnyIo(string area)
    {
        using var fixture = CreateFixture();
        await Assert.ThrowsAsync<GateToolException>(() => fixture.Tools.ListStates(CancellationToken.None, area: area));
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Equal(0, fixture.Secrets.Calls);
    }

    [Fact]
    public async Task ConflictingMembershipFailsClosedInsteadOfChoosingFirstArea()
    {
        using var fixture = CreateFixture("""
            [{"area_id":"a","area_name":"A","entities":["sensor.room"]},
             {"area_id":"b","area_name":"B","entities":["sensor.room"]}]
            """);
        await Assert.ThrowsAsync<GateToolException>(() => fixture.Tools.GetState("sensor.room", CancellationToken.None));
    }

    [Fact]
    public async Task EmptyAreasGiveNullInDetailAndAnEmptyAreaList()
    {
        using var fixture = CreateFixture("[]");
        var detail = (await fixture.Tools.GetState("sensor.room", CancellationToken.None)).StructuredContent!.Value;
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("area_name").ValueKind);
        var areas = (await fixture.Tools.ListAreas(CancellationToken.None)).StructuredContent!.Value;
        Assert.Empty(areas.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task EmptyReadAllowlistDoesNotExposeAreaEntities()
    {
        using var fixture = CreateFixture(domains: []);
        var result = (await fixture.Tools.ListAreas(CancellationToken.None)).StructuredContent!.Value;
        Assert.All(result.GetProperty("items").EnumerateArray(), item => Assert.Empty(item.GetProperty("entities").EnumerateArray()));
    }

    [Fact]
    public async Task AreaFilterIsAppliedBeforeTheHundredStateLimit()
    {
        using var fixture = CreateFixture();
        var normal = fixture.Handler.Respond!;
        fixture.Handler.Respond = (request, ct) => request.RequestUri!.AbsolutePath != "/api/states" ? normal(request, ct) :
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Enumerable.Range(0, 110).Select(i => new { entity_id = $"sensor.a{i}", state = "1", attributes = new { } })
                    .Cast<object>().Append(States[0]))
            });
        var result = (await fixture.Tools.ListStates(CancellationToken.None, "sensor", "living")).StructuredContent!.Value;
        Assert.Equal(1, result.GetProperty("returned_count").GetInt32());
        Assert.Equal("sensor.room", Assert.Single(result.GetProperty("items").EnumerateArray()).GetProperty("entity_id").GetString());
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("ha_control", "light", "turn_on")]
    [InlineData("ha_sensitive_control", "lock", "unlock")]
    public async Task ReadingAreasDoesNotPermitAreaControl(string tool, string domain, string service)
    {
        await using var fixture = new ControlFixture();
        using var result = await fixture.Call(tool, domain, service, "{\"area_id\":\"living\"}");
        Assert.True(ControlFixture.Failed(result));
        Assert.Empty(fixture.Calls);
        Assert.Empty(fixture.Channel.Requests);
        Assert.Equal(0, fixture.Secrets.Calls);
    }

    private const string Areas = """
        [{"area_id":"living","area_name":"Nappali","entities":["sensor.room","light.desk","lock.front","sensor.gps_accuracy","sensor.unsafe","sensor.missing"]},
         {"area_id":"empty","area_name":"Üres","entities":[]}]
        """;
    private static readonly object[] States =
    [
        new { entity_id = "sensor.room", state = "21", attributes = new { friendly_name = "Hőmérő" } },
        new { entity_id = "light.desk", state = "on", attributes = new { friendly_name = "Lámpa" } },
        new { entity_id = "lock.front", state = "locked", attributes = new { friendly_name = "Titkos" } },
        new { entity_id = "sensor.gps_accuracy", state = "1", attributes = new { friendly_name = "Rejtett" } },
        new { entity_id = "sensor.unsafe", state = "47.123,19.456", attributes = new { friendly_name = "Rejtett" } },
        new { entity_id = "sensor.outside", state = "1", attributes = new { friendly_name = "Kinti" } }
    ];

    internal static HomeAssistantReadToolsTests.Fixture CreateFixture(string areas = Areas, string[]? domains = null)
    {
        var fixture = new HomeAssistantReadToolsTests.Fixture("{}", domains);
        fixture.Handler.Respond = (request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath == "/api/template" ? new StringContent(areas) :
                JsonContent.Create(request.RequestUri.AbsolutePath == "/api/states" ? States : States[0])
        });
        return fixture;
    }
}
