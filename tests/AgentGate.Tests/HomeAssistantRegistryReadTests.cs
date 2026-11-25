using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.Registry;
using AgentGate.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantRegistryReadTests
{
    [Fact]
    public async Task LabelListAndEntityReadsKeepMinimalFilteredProjection()
    {
        using var f = new HomeAssistantRegistryTests.Fixture("""
            [{"entity_id":"sensor.room","state":"21","attributes":{"friendly_name":"Szoba","secret_extra":"nem adható ki"}},
             {"entity_id":"lock.door","state":"locked","attributes":{}},
             {"entity_id":"sensor.gps","state":"47.123,19.456","attributes":{}}]
            """);
        using var services = new ServiceCollection().AddSingleton(f.Client).AddSingleton(f.Policy).BuildServiceProvider();
        var tools = new HomeAssistantTools(services);
        var listed = (await tools.ListStates(default)).StructuredContent!.Value;
        Assert.Equal(1, listed.GetProperty("returned_count").GetInt32());
        Assert.Equal("keep", listed.GetProperty("items")[0].GetProperty("labels")[0].GetString());
        Assert.DoesNotContain("secret_extra", listed.GetRawText());
        Assert.Equal(1, f.Socket.Calls);
        var labels = await f.Client.GetLabelsAsync();
        Assert.Equal(2, labels.GetProperty("returned_count").GetInt32());
        Assert.All(labels.GetProperty("items").EnumerateArray(), item =>
            Assert.Equal(new[] { "label_id", "name" }, item.EnumerateObject().Select(property => property.Name).Order()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteUpstreamEntitySchemaSupportsPreparationAndLabelReadsUnlessUnknownFieldsExist(bool unknownField)
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        // HA extended_dict: as_partial_dict és az öt bővített mező; éles HA nélkül.
        var entity = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
            {"area_id":null,"categories":{},"config_entry_id":"entry","config_subentry_id":null,
             "created_at":1700000000,"device_id":null,"disabled_by":null,"entity_category":null,
             "entity_id":"sensor.room","hidden_by":null,"icon":null,"id":"registry_entry",
             "labels":["keep"],"modified_at":1700000000,"name":"Régi","next_name_part":null,
             "options":{},"original_name":"Szoba","platform":"test","translation_key":null,
             "unique_id":"room","aliases":[],"capabilities":null,"device_class":null,
             "original_device_class":null,"original_icon":null}
            """)!;
        if (unknownField) entity.Add("unrecognized", JsonSerializer.SerializeToElement(true));
        f.Socket.RequestBody = _ => JsonSerializer.Serialize(entity);
        var arguments = HomeAssistantRegistryTests.Arguments("{\"name\":\"Új\"}");
        if (unknownField)
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await f.Operation.PrepareAsync(arguments, default));
            await Assert.ThrowsAnyAsync<Exception>(() => f.Client.GetEntityLabelsAsync("sensor.room"));
        }
        else
        {
            var operation = await f.Operation.PrepareAsync(arguments, default);
            using var before = JsonDocument.Parse(operation.Parameters["before"]!);
            Assert.Equal("Régi", before.RootElement.GetProperty("name").GetString());
            Assert.False(before.RootElement.TryGetProperty("next_name_part", out _));
            Assert.Equal(new[] { "keep" }, await f.Client.GetEntityLabelsAsync("sensor.room"));
        }
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("{\"HomeAssistantRegistry:Enabled\":\"invalid\"}")]
    [InlineData("{\"HomeAssistantRegistry:Unknown\":\"true\"}")]
    [InlineData("{\"HomeAssistantRegistry:EntityDenylist:1\":\"sensor.room\"}")]
    public void InvalidConfigurationDoesNotEnableWrites(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string?>>(json)!;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Throws<OptionsValidationException>(() => RegistryPolicy.FromConfiguration(config));
    }

    [Fact]
    public async Task DisabledByDefaultAndKnownTokenInOldValueNeverAuthorizeAWrite()
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        var policy = RegistryPolicy.FromConfiguration(new ConfigurationBuilder().Build());
        var disabled = new HomeAssistantRegistryService(f.Client, policy, f.KillSwitch);
        await Assert.ThrowsAnyAsync<Exception>(() => disabled.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"name\":\"Új\"}"), default));
        Assert.Equal(0, f.Socket.Calls);
        f.Name = f.Secrets.Token;
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () => await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"name\":\"Új\"}"), default));
        Assert.DoesNotContain(f.Secrets.Token, exception.ToString());
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task UnknownPrestateFieldsFailClosedRatherThanAssumingSchemaCompatibility()
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        f.Socket.Frame = index => System.Text.Encoding.UTF8.GetBytes(index switch
        {
            0 => "{\"type\":\"auth_required\"}",
            1 => "{\"type\":\"auth_ok\"}",
            _ => """{"id":1,"type":"result","success":true,"result":{"entity_id":"sensor.room","device_id":"dev","area_id":null,"labels":["keep"],"name":"Old","icon":null,"hidden_by":null,"disabled_by":null,"unrecognized":true}}"""
        });
        await Assert.ThrowsAnyAsync<Exception>(async () => await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"name\":\"Új\"}"), default));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task DuplicateOrMissingPrestateCannotMasqueradeAsComplete()
    {
        using var f = new HomeAssistantRegistryTests.Fixture();
        f.Socket.Frame = index => System.Text.Encoding.UTF8.GetBytes(index switch
        {
            0 => "{\"type\":\"auth_required\"}",
            1 => "{\"type\":\"auth_ok\"}",
            _ => "{\"id\":1,\"type\":\"result\",\"success\":true,\"result\":{\"entity_id\":\"sensor.room\",\"name\":\"a\",\"name\":\"b\"}}"
        });
        await Assert.ThrowsAnyAsync<Exception>(async () => await f.Operation.PrepareAsync(HomeAssistantRegistryTests.Arguments("{\"name\":\"Új\"}"), default));
        Assert.Equal(0, f.Writes);
    }
}
