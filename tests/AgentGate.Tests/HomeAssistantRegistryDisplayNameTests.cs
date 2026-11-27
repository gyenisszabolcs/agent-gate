using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Approval.HomeAssistant;
using AgentGate.Tools;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantRegistryDisplayNameTests
{
    [Theory]
    [InlineData(null, "Eredeti név", null, null, "Eredeti név")]
    [InlineData("Led", "Környezetfény", "Yeelight étkező", null, "Yeelight étkező Led")]
    [InlineData(null, null, "Gyári név", "Yeelight étkező", "Yeelight étkező")]
    [InlineData(null, "IKEA Konyha LED", "IKEA Konyha LED", null, "IKEA Konyha LED IKEA Konyha LED")]
    [InlineData(null, null, null, null, "sensor.room")]
    public async Task RegistryFallbackWorksWithoutStateAndPreservesNullableSnapshot(string? name, string? original,
        string? deviceName, string? deviceUserName, string expected)
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture
        {
            Name = name, OriginalName = original, Device = deviceName is null ? null : "device",
            DeviceName = deviceName, DeviceNameByUser = deviceUserName,
            DisabledBy = "user"
        };
        fixture.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        var arguments = HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}");
        var single = await fixture.Operation.PrepareAsync(arguments, default);
        var batch = await fixture.Operation.PrepareAsync(HomeAssistantRegistryBatchTests.Batch(arguments), default);
        Assert.Equal(expected, single.Presentation!.TargetName);
        Assert.Equal(expected, batch.Presentation!.Items![0].TargetName);
        using var before = JsonDocument.Parse(single.Parameters["before"]!);
        Assert.Equal(name, before.RootElement.GetProperty("name").GetString());
        Assert.Equal(0, fixture.Writes);
        Assert.Equal(0, fixture.Http.Calls);
    }

    // A registry név és az original_name külön snapshot/megjelenítési adat marad.
    [Theory]
    [InlineData("Led", "Led")]
    [InlineData(null, "Eredeti név")]
    [InlineData("Felülírt", "Felülírt")]
    public async Task SingleUsesHomeAssistantDisplayWithoutReplacingRegistryName(string? name, string display)
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = name, OriginalName = display };
        var operation = await fixture.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}"), default);
        Assert.Equal(display, operation.Presentation!.TargetName);
        Assert.Null(operation.Presentation.EntityIdFallback);
        using var before = JsonDocument.Parse(operation.Parameters["before"]!);
        Assert.Equal(name, before.RootElement.GetProperty("name").GetString());
        Assert.DoesNotContain("friendly_name", operation.Parameters["before"]!);
        Assert.DoesNotContain(display, operation.Parameters["changes"]!);
        Assert.Contains(display, operation.Presentation.RegistryLine());
        Assert.Contains(display, new ApprovalPushFormatter().Request(Notice(operation), "absent-token"));
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"friendly_name\":null}")]
    public async Task MissingDisplayUsesBoundCanonicalEntityIdForSingleAndBatch(string attributes)
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = null };
        fixture.Http.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { entity_id = "sensor.room", state = "on", attributes = JsonSerializer.Deserialize<JsonElement>(attributes) }))
        });
        var arguments = HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}");
        var single = await fixture.Operation.PrepareAsync(arguments, default);
        Assert.Equal("sensor.room", single.Presentation!.RequireRegistryTarget());
        Assert.Equal("sensor.room", single.Presentation.EntityIdFallback);
        Assert.Equal(0, fixture.Http.Calls);
        Assert.Contains("sensor.room", new ApprovalPushFormatter().Request(Notice(single), "absent-token"));
        var batch = await fixture.Operation.PrepareAsync(HomeAssistantRegistryBatchTests.Batch(arguments), default);
        Assert.Equal(single.Presentation.TargetName, batch.Presentation!.Items![0].TargetName);
        Assert.Contains("sensor.room", new ApprovalPushFormatter().RegistryBatchDetails(Notice(batch), "absent-token"));
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData("password=canary")]
    [InlineData("Név\nMásik sor")]
    [InlineData(" ")]

    [InlineData(" név")]
    [InlineData("sensor.room")]
    [InlineData("Név@cím")]
    [InlineData("1234567890abcdef1234567890abcdef")]
    public async Task UnsafePresentDisplayNeverFallsBackAndReturnsActualSafeReason(string display)
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = null, OriginalName = display };
        var error = await Assert.ThrowsAsync<GateToolException>(async () => await fixture.Operation.PrepareAsync(
            HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}"), default));
        Assert.Equal(ApprovalPresentation.InvalidNameMessage, error.Message);
        Assert.NotEqual(ApprovalPresentation.SplitRequestMessage, error.Message);
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData("Szoba", "0123456789abcdef", null, ApprovalPresentation.InvalidNameMessage)]
    [InlineData("0123456789abcdef", "Hőmérő", null, ApprovalPresentation.InvalidNameMessage)]
    [InlineData("Szoba", "", null, ApprovalPresentation.InvalidNameMessage)]
    [InlineData("Szoba", "Hőmérő", "", ApprovalPresentation.InvalidNameMessage)]
    [InlineData("Huszonöt betűs eszköznév", "Huszonöt betűs entitásnév ma", null, ApprovalPresentation.LongNameMessage)]
    public async Task EveryNameComponentAndCombinedNameMustPassValidation(string deviceName, string entityName,
        string? deviceUserName, string expectedMessage)
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture
        {
            Name = null, OriginalName = entityName, Device = "device",
            DeviceName = deviceName, DeviceNameByUser = deviceUserName
        };
        var arguments = HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}");
        var single = await Assert.ThrowsAsync<GateToolException>(async () => await fixture.Operation.PrepareAsync(arguments, default));
        var batch = await Assert.ThrowsAsync<GateToolException>(async () =>
            await fixture.Operation.PrepareAsync(HomeAssistantRegistryBatchTests.Batch(arguments), default));
        Assert.Equal(expectedMessage, single.Message);
        Assert.Equal(expectedMessage, batch.Message);
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("true")]
    public async Task WrongDisplayTypeCannotBecomeEntityIdFallback(string value)
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = null };
        var original = fixture.Socket.RequestBody!;
        fixture.Socket.RequestBody = command => original(command).Replace("\"original_name\":null", "\"original_name\":" + value, StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<GateToolException>(async () => await fixture.Operation.PrepareAsync(
            HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}"), default));
        Assert.Equal(ApprovalPresentation.InvalidNameMessage, error.Message);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task OversizedDisplayHasDedicatedReasonRatherThanFallbackOrSplit()
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = null, OriginalName = new string('á', 49) };
        var error = await Assert.ThrowsAsync<GateToolException>(async () => await fixture.Operation.PrepareAsync(
            HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}"), default));
        Assert.Equal(ApprovalPresentation.LongNameMessage, error.Message);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task DisplayOnlyChangeDoesNotChangeBoundParametersOrExecutionPrestate()
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = null, OriginalName = "Első név" };
        var arguments = HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}");
        var first = await fixture.Operation.PrepareAsync(arguments, default);
        fixture.OriginalName = "Második név";
        var second = await fixture.Operation.PrepareAsync(arguments, default);
        Assert.Equal(JsonSerializer.Serialize(first.Parameters), JsonSerializer.Serialize(second.Parameters));
        Assert.NotEqual(first.Presentation!.TargetName, second.Presentation!.TargetName);
        await fixture.Operation.ExecuteAsync(first, default);
        Assert.Equal(1, fixture.Writes);
        Assert.False(fixture.LastUpdate.TryGetProperty("name", out _));
        Assert.Equal(new[] { "keep", "new" }, fixture.LastUpdate.GetProperty("labels").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task NullRegistryNameStillBindsExecutionPrestate()
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = null, OriginalName = "Megjelenített név" };
        var operation = await fixture.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}"), default);
        fixture.Name = "Időközben átnevezett";
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Operation.ExecuteAsync(operation, default));
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task CancellationDuringRegistryReadDoesNotWrite()
    {
        using var source = new CancellationTokenSource();
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = null };
        var original = fixture.Socket.RequestBody!;
        fixture.Socket.RequestBody = command => { source.Cancel(); return original(command); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fixture.Operation.PrepareAsync(
            HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}"), source.Token));
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task RegistryReadFailureIsNotHiddenByEntityIdFallback()
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = null };
        fixture.Socket.RequestBody = _ => throw new InvalidOperationException("provider-canary");
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Operation.PrepareAsync(
            HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}"), default));
        Assert.DoesNotContain("provider-canary", error.Message);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public void EntityIdFallbackCannotBeReboundToAnotherTargetOrLeakToken()
    {
        var changes = new Dictionary<string, ApprovalDisplayChange> { ["name"] = new("Régi", "Új") };
        Assert.Throws<GateToolException>(() => new ApprovalPresentation("sensor.room", changes, entityIdFallback: "sensor.other").RequireRegistryTarget());
        Assert.Throws<GateToolException>(() => new ApprovalPresentation("sensor.room", changes, entityIdFallback: "sensor.room").RequireRegistryTarget("room"));
    }

    [Fact]
    public void ExtendedRowLimitReportsTheRowLimitNotTheFullByteLimit()
    {
        var value = string.Join(", ", Enumerable.Repeat(new string('á', 40), 110));
        var display = new ApprovalPresentation("Lámpa", new Dictionary<string, ApprovalDisplayChange> { ["labels"] = new(value, value) });
        var error = Assert.Throws<GateToolException>(() => display.RegistryLine(extended: true));
        Assert.Contains("8192", error.Message);
        Assert.DoesNotContain("32 KiB", error.Message);
    }

    [Fact]
    public async Task FullBatchUtf8LimitIsRejectedDuringPreparationWithDedicatedReason()
    {
        using var fixture = new HomeAssistantRegistryBatchTests.BatchFixture();
        var keep = Enumerable.Range(0, 15).Select(index => "keep_" + index).ToArray();
        fixture.Device.Labels = JsonSerializer.Serialize(keep.Append("new").Select((id, index) => new
        {
            label_id = id, name = new string('á', 40) + (char)('A' + index)
        }));
        var original = fixture.Device.Socket.RequestBody!;
        fixture.Device.Socket.RequestBody = command => command.GetProperty("type").GetString() == "config/entity_registry/get"
            ? JsonSerializer.Serialize(new
            {
                entity_id = command.GetProperty("entity_id").GetString(), device_id = (string?)null, area_id = (string?)null,
                labels = keep, name = (string?)null, icon = (string?)null, hidden_by = (string?)null, disabled_by = (string?)null
            }) : original(command);
        var members = Enumerable.Range(0, 15).Select(index => HomeAssistantRegistryTests.Arguments(
            "{\"labels_add\":[\"new\"]}", "sensor.item" + index)).ToArray();
        var error = await Assert.ThrowsAsync<GateToolException>(async () => await fixture.Handler.PrepareAsync(
            HomeAssistantRegistryBatchTests.Batch(members), default));
        Assert.Equal(ApprovalPresentation.FullLimitMessage, error.Message);
        Assert.Empty(fixture.Writes);
    }

    [Fact]
    public async Task NullNameDisplayDoesNotBypassExecutionKillSwitch()
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = null, OriginalName = "Megjelenített név" };
        var operation = await fixture.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}"), default);
        fixture.KillSwitch.Enabled = true;
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Operation.ExecuteAsync(operation, default));
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task RuntimeBearerInDisplayIsRejectedWithoutEchoOrFallback()
    {
        using var fixture = new HomeAssistantRegistryTests.Fixture { Name = null };
        fixture.OriginalName = fixture.Secrets.Token;
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Operation.PrepareAsync(
            HomeAssistantRegistryTests.Arguments("{\"labels_add\":[\"new\"]}"), default));
        Assert.DoesNotContain(fixture.Secrets.Token, error.Message);
        Assert.NotEqual(ApprovalPresentation.SplitRequestMessage, error.Message);
        Assert.Equal(0, fixture.Writes);
    }

    private static ApprovalNotice Notice(PreparedOperation operation) => new(Guid.NewGuid(), "Mikaela", operation.Tool, operation.Risk,
        JsonSerializer.Serialize(operation.Parameters), DateTimeOffset.MaxValue, operation.Presentation);
}
