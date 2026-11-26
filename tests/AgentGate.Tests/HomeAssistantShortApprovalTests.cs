
using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Approval.HomeAssistant;
using AgentGate.Tools;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Fact]
    public async Task RegistryMetadataCannotHideOneOfTheBoundChanges()
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"name\":\"Új\",\"labels_add\":[\"new\"]}"), default);
        await using var h = new Standalone();
        await h.StartAsync();
        var notice = Notice() with
        {
            Tool = op.Tool,
            Summary = JsonSerializer.Serialize(op.Parameters),
            Presentation = new("Régi", new Dictionary<string, ApprovalDisplayChange> { ["name"] = new("Régi", "Új") })
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Channel.SendRequestAsync(notice, default));
        Assert.Equal(0, h.Http.Count);
        Assert.Equal(0, NotificationCount(h.Channel));
    }

    [Theory]
    [InlineData("{\"area_id\":\"room\"}", "terület módosítása", "Terület: Nincs → Szoba (saját)")]
    [InlineData("{\"labels_add\":[\"new\"]}", "címkék módosítása", "Címkék: Megmarad → Megmarad, Új")]
    [InlineData("{\"hidden_by\":\"user\"}", "elrejtés módosítása", "Elrejtés: Nem → Igen")]
    [InlineData("{\"name\":\"Új\",\"labels_add\":[\"new\"]}", "nyilvántartás módosítása", "Címkék: Megmarad → Megmarad, Új; Név: Régi → Új")]
    public async Task RegistryPushResolvesEntireChangeWithoutTechnicalValues(string patch, string action, string expected)
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments(patch), default);
        var withoutDisplay = new PreparedOperation(op.Tool, op.Risk, op.Parameters, op.RequiresLosslessAudit);
        Assert.Equal(withoutDisplay.ComputeHash("Mikaela"), op.ComputeHash("Mikaela"));
        var summary = JsonSerializer.Serialize(op.Parameters);
        var notice = Notice() with { Tool = op.Tool, Summary = summary, Presentation = op.Presentation };
        var (title, message) = new ApprovalPushFormatter().Format(notice, f.Secrets.Token);
        Assert.Equal("Jóváhagyás: " + action, title);
        Assert.Equal("Régi · " + expected, message.Split('\n')[0]);
        Assert.Equal(2, message.Split('\n').Length);
        Assert.DoesNotContain("sensor.room", message);
        Assert.DoesNotContain("area_id", message);
        Assert.DoesNotContain("label_id", message);
        Assert.DoesNotContain("keep", message);
        Assert.DoesNotContain("new", message);
        Assert.DoesNotContain(op.ComputeHash("Mikaela"), message);
        Assert.Equal(summary, notice.Summary);
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("long-diff")]
    [InlineData("technical-name")]
    [InlineData("icon")]
    [InlineData("missing-label-name")]
    [InlineData("ambiguous-area")]
    public async Task UnrepresentableRegistryChangeIsRejectedWithFixedSplitInstruction(string defect)
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        var patch = "{\"name\":\"Új\"}";
        switch (defect)
        {
            case "long-diff":
                f.Name = new string('Ő', 40);
                patch = JsonSerializer.Serialize(new { name = new string('Ű', 40), area_id = "room", labels_add = new[] { "new" } });
                break;
            case "technical-name": patch = "{\"name\":\"sensor.room\"}"; break;
            case "icon": patch = "{\"icon\":\"mdi:home\"}"; break;
            case "missing-label-name": f.LabelsCatalog = "[{\"label_id\":\"keep\",\"name\":\"Megmarad\"},{\"label_id\":\"new\"}]"; patch = "{\"labels_add\":[\"new\"]}"; break;
            case "ambiguous-area": f.AreasCatalog = "[{\"area_id\":\"room\",\"name\":\"Szoba\"},{\"area_id\":\"other\",\"name\":\"Szoba\"}]"; patch = "{\"area_id\":\"room\"}"; break;
        }
        await using var app = new ChannelApp { RegistryFixture = f };
        using var client = app.CreateClient();
        using var intent = JsonDocument.Parse(patch);
        using var reply = await CallMcpAsync(app, "ha_update_registry", new { entity_id = "sensor.room", patch = intent.RootElement });
        Assert.True(reply.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains(ApprovalPresentation.SplitRequestMessage, reply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(0, app.Http.Count);
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("canonical-area-catalog")]
    [InlineData("canonical-label-catalog")]
    [InlineData("canonical-name-change")]
    [InlineData("sentinel-area-before")]
    [InlineData("sentinel-area-after")]
    [InlineData("sentinel-label-before")]
    [InlineData("sentinel-label-after")]
    [InlineData("sentinel-name-before")]
    [InlineData("sentinel-name-after")]
    [InlineData("sentinel-name-before-nfd")]
    public async Task RegistryConsentAmbiguityIsRejectedDuringPreparation(string defect)
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        var patch = "{\"name\":\"Új\"}";
        switch (defect)
        {
            case "canonical-area-catalog":
                f.Area = "old";
                f.AreasCatalog = JsonSerializer.Serialize(new[] { new { area_id = "old", name = "Átjáró" }, new { area_id = "room", name = "A\u0301tjáró" } });
                patch = "{\"area_id\":\"room\"}";
                break;
            case "canonical-label-catalog":
                f.LabelsCatalog = JsonSerializer.Serialize(new[] { new { label_id = "keep", name = "Átjáró" }, new { label_id = "new", name = "A\u0301tjáró" } });
                patch = "{\"labels_remove\":[\"keep\"],\"labels_add\":[\"new\"]}";
                break;
            case "canonical-name-change":
                f.Name = "Átjáró";
                patch = JsonSerializer.Serialize(new { name = "A\u0301tjáró" });
                break;
            case "sentinel-area-before":
            case "sentinel-area-after":
                f.Area = "old";
                f.AreasCatalog = JsonSerializer.Serialize(new[]
                {
                    new { area_id = "old", name = defect == "sentinel-area-before" ? "Nincs" : "Szoba" },
                    new { area_id = "room", name = defect == "sentinel-area-after" ? "Nincs" : "Szoba" }
                });
                patch = "{\"area_id\":\"room\"}";
                break;
            case "sentinel-label-before":
            case "sentinel-label-after":
                f.LabelsCatalog = JsonSerializer.Serialize(new[]
                {
                    new { label_id = "keep", name = defect == "sentinel-label-before" ? "Nincs" : "Megmarad" },
                    new { label_id = "new", name = defect == "sentinel-label-after" ? "Nincs" : "Új" }
                });
                patch = "{\"labels_remove\":[\"keep\"],\"labels_add\":[\"new\"]}";
                break;
            case "sentinel-name-before": f.Name = "Alapértelmezett"; break;
            case "sentinel-name-after": patch = "{\"name\":\"Alapértelmezett\"}"; break;
            case "sentinel-name-before-nfd": f.Name = "Alape\u0301rtelmezett"; break;
            default: throw new InvalidOperationException("Ismeretlen teszteset.");
        }
        var error = await Assert.ThrowsAsync<GateToolException>(async () =>
            await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments(patch), default));
        Assert.Equal(ApprovalPresentation.SplitRequestMessage, error.Message);
        await using var app = new ChannelApp { RegistryFixture = f };
        using var client = app.CreateClient();
        using var intent = JsonDocument.Parse(patch);
        using var reply = await CallMcpAsync(app, "ha_update_registry", new { entity_id = "sensor.room", patch = intent.RootElement });
        Assert.True(reply.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(ApprovalPresentation.SplitRequestMessage, reply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(0, app.Http.Count);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task RegistryCanonicalDisplayPreservesRawBoundIntent()
    {
        using var f = new HomeAssistantRegistryTests.Fixture { Name = "Re\u0301gi" };
        var newName = "U\u0301j";
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments(JsonSerializer.Serialize(new { name = newName })), default);
        Assert.Equal(new ApprovalDisplayChange("Régi", "Új"), op.Presentation!.Changes["name"]);
        using var before = JsonDocument.Parse(op.Parameters["before"]!);
        using var after = JsonDocument.Parse(op.Parameters["after"]!);
        using var patch = JsonDocument.Parse(op.Parameters["patch"]!);
        Assert.Equal(f.Name, before.RootElement.GetProperty("name").GetString());
        Assert.Equal(newName, after.RootElement.GetProperty("name").GetString());
        Assert.Equal(newName, patch.RootElement.GetProperty("name").GetString());
        var withoutDisplay = new PreparedOperation(op.Tool, op.Risk, op.Parameters, op.RequiresLosslessAudit);
        Assert.Equal(withoutDisplay.ComputeHash("Mikaela"), op.ComputeHash("Mikaela"));
        f.Name = "Régi";
        var canonicalIntent = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"name\":\"Új\"}"), default);
        Assert.Equal(op.Presentation.Changes["name"], canonicalIntent.Presentation!.Changes["name"]);
        Assert.NotEqual(op.ComputeHash("Mikaela"), canonicalIntent.ComputeHash("Mikaela"));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task RegistryCanonicalLabelDisplayOrdersNamesNotTechnicalIds()
    {
        using var f = new HomeAssistantRegistryTests.Fixture
        {
            LabelsCatalog = JsonSerializer.Serialize(new[] { new { label_id = "keep", name = "A\u0301tjáró" }, new { label_id = "new", name = "Zebra" } })
        };
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}"), default);
        Assert.Equal(new ApprovalDisplayChange("Átjáró", "Zebra, Átjáró"), op.Presentation!.Changes["labels"]);
        using var after = JsonDocument.Parse(op.Parameters["after"]!);
        Assert.Equal(new[] { "keep", "new" }, after.RootElement.GetProperty("labels").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("{\"name\":null}", "name", "Régi", "Alapértelmezett")]
    [InlineData("{\"area_id\":\"room\"}", "area_id", "Nincs", "Szoba (saját)")]
    [InlineData("{\"labels_remove\":[\"keep\"]}", "labels", "Megmarad", "Nincs")]
    [InlineData("{\"hidden_by\":\"user\"}", "hidden_by", "Nem", "Igen")]
    [InlineData("{\"disabled_by\":\"user\"}", "disabled_by", "Nem", "Igen")]
    public async Task RegistrySentinelAndBooleanStatesRemainRepresentable(string patch, string field, string before, string after)
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        var op = await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments(patch), default);
        Assert.Equal(new ApprovalDisplayChange(before, after), op.Presentation!.Changes[field]);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public void HumanNameRejectsInvalidUnicodeAndLongCombiningSequences()
    {
        Assert.False(ApprovalPresentation.IsHumanName("Nappali\ufffd"));
        Assert.False(ApprovalPresentation.IsHumanName("Nappali\ud800"));
        Assert.False(ApprovalPresentation.IsHumanName("Nappali" + new string('\u0301', 200)));
        Assert.False(ApprovalPresentation.IsHumanName(new string('Ő', 49)));
        Assert.True(ApprovalPresentation.IsHumanName(new string('Ő', 48)));
        Assert.True(ApprovalPresentation.IsHumanName("Nappali 🔊"));
    }
}
