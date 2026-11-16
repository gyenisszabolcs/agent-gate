using System.Text;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Policy;
using AgentGate.Secrets;
using AgentGate.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantControlValidationTests
{
    public static IEnumerable<object[]> InvalidData()
    {
        foreach (var value in new[] { "-1", "101", "\"50\"", "null", "true", "{}", "[]", "1e400" }) yield return ["light", "turn_on", "{\"brightness_pct\":" + value + "}"];
        foreach (var value in new[] { "1999", "6501", "2000.5", "2000.0", "\"2000\"", "null", "true", "{}" }) yield return ["light", "toggle", "{\"color_temp_kelvin\":" + value + "}"];
        foreach (var value in new[] { "-1", "101", "1.5", "1.0", "\"1\"", "null", "false", "{}" }) yield return ["cover", "set_cover_position", "{\"position\":" + value + "}"];
        foreach (var value in new[] { "9.99", "30.01", "\"20\"", "null", "true", "[]", "1e400" }) yield return ["climate", "set_temperature", "{\"temperature\":" + value + "}"];
        foreach (var value in new[] { "\"unknown\"", "\"HEAT\"", "1", "null", "[]" }) yield return ["climate", "set_hvac_mode", "{\"hvac_mode\":" + value + "}"];
        foreach (var data in new[] { "[]", "1", "true", "\"x\"", "{\"brightness_pct\":50,\"brightness_pct\":60}", "{\"brightness_pct\":50,\"unknown\":1}", "{\"target\":{\"entity_id\":\"all\"}}", "{\"entity_id\":\"light.other\"}", "{\"domain\":\"light\"}", "{\"service\":\"turn_on\"}", "{\"area_id\":\"room\"}", "{\"device_id\":\"room\"}" }) yield return ["light", "turn_on", data];
        foreach (var service in new[] { "set_cover_position", "set_temperature", "set_hvac_mode" }) yield return [service == "set_cover_position" ? "cover" : "climate", service, "{}"];
        foreach (var pair in new[] { ("light", "turn_off"), ("switch", "toggle"), ("climate", "turn_on"), ("climate", "turn_off"), ("cover", "open_cover"), ("scene", "turn_on"), ("input_boolean", "toggle") }) yield return [pair.Item1, pair.Item2, "{\"brightness_pct\":1}"];
        yield return ["climate", "set_temperature", "{\"temperature\":20,\"min_temp\":10}"];
        yield return ["climate", "set_temperature", "{\"temperature\":20,\"temperature_unit\":\"°C\"}"];
    }
    [Theory]
    [MemberData(nameof(InvalidData))]
    public async Task MalformedDataNeverReachesHttpOrPush(string domain, string service, string data)
    {
        await using var f = new ControlFixture();
        foreach (var tool in new[] { "ha_control", "ha_sensitive_control" })
        {
            using var reply = await f.Call(tool, domain, service, data);
            Assert.True(ControlFixture.Failed(reply));
        }
        Assert.Empty(f.Calls);
        Assert.Empty(f.Channel.Requests);
        Assert.Equal(0, f.Secrets.Calls);
    }
    [Theory]
    [InlineData("alarm_control_panel", "alarm_disarm")]
    [InlineData("light", "create")]
    [InlineData("homeassistant", "turn_on")]
    [InlineData("script", "turn_on")]
    [InlineData("light", "unknown")]
    public async Task UnlistedDomainOrServiceCannotBecomeAGenericServiceCaller(string domain, string service)
    {
        await using var f = new ControlFixture();
        foreach (var tool in new[] { "ha_control", "ha_sensitive_control" })
        {
            using var reply = await f.Call(tool, domain, service);
            Assert.True(ControlFixture.Failed(reply));
        }
        Assert.Empty(f.Calls);
        Assert.Empty(f.Channel.Requests);
        Assert.Equal(0, f.Secrets.Calls);
    }

    [Fact]
    public async Task GlobalEntityDenyOverridesBothToolsBeforeAnyFreshLookup()
    {
        var policy = HomeAssistantControlPolicyTests.Policy("{\"HomeAssistantControl\":{\"Domains\":{\"cover\":{\"AllowAllEntities\":true,\"ControlServices\":[\"open_cover\"],\"SensitiveControlServices\":[\"open_cover\"]}},\"EntityDenylist\":[\"cover.room\"]}}");
        await using var f = new ControlFixture(services => services.AddSingleton(policy));
        foreach (var tool in new[] { "ha_control", "ha_sensitive_control" })
        {
            using var reply = await f.Call(tool, "cover", "open_cover");
            Assert.True(ControlFixture.Failed(reply));
        }
        Assert.Empty(f.Calls);
        Assert.Empty(f.Channel.Requests);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("light.*")]
    [InlineData("light.a,light.b")]
    [InlineData("light.a light.b")]
    [InlineData("light.a\n")]
    [InlineData("Light.room")]
    [InlineData("light.")]
    [InlineData("switch.room")]
    [InlineData("light.room/../config")]
    public void NonCanonicalOrCrossDomainTargetsAreRejected(string entity) =>
        Assert.Throws<GateToolException>(() => HomeAssistantControlPolicyTests.Policy().Validate(HomeAssistantControlPolicyTests.Arguments("light", "turn_on", entity: entity), RiskLevel.Control));

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"enabled\"")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"Domains\":\"light\"}")]
    [InlineData("{\"Domains\":{\"alarm_control_panel\":{}}}")]
    [InlineData("{\"Domains\":{\"switch\":{\"AllowAllEntities\":true}}}")]
    [InlineData("{\"Domains\":{\"light\":{\"AllowAllEntities\":true,\"EntityAllowlist\":[\"light.a\"]}}}")]
    [InlineData("{\"Domains\":{\"light\":{\"EntityAllowlist\":[\"switch.a\"]}}}")]
    [InlineData("{\"Domains\":{\"light\":{\"AllowAllEntities\":null}}}")]
    [InlineData("{\"Domains\":{\"light\":{\"ControlServices\":[\"unlock\"]}}}")]
    [InlineData("{\"Domains\":{\"lock\":{\"ControlServices\":[\"unlock\"]}}}")]
    [InlineData("{\"Domains\":{\"light\":{\"SensitiveControlServices\":[\"turn_on\"]}}}")]
    [InlineData("{\"Domains\":{\"cover\":{\"SensitiveControlServices\":[\"set_cover_position\"]}}}")]
    [InlineData("{\"EntityDenylist\":[\"all\"]}")]
    [InlineData("{\"HvacModeAllowlist\":[\"unknown\"]}")]
    [InlineData("{\"RateLimit\":{\"PerEntityPerMinute\":null}}")]
    [InlineData("{\"RateLimit\":{\"PerEntityPerMinute\":0}}")]
    [InlineData("{\"RateLimit\":{\"GlobalPerMinute\":-1}}")]
    [InlineData("{\"RateLimit\":{\"GlobalPerMinute\":2147483648}}")]
    [InlineData("{\"RateLimit\":{\"GlobalPerMinute\":1.5}}")]
    [InlineData("{\"RateLimit\":{\"GlobalPerMinute\":\"bad\"}}")]
    public async Task PresentMalformedConfigurationFailsAtActualHostStartupWithoutSecretLookup(string section)
    {
        using var config = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("{\"HomeAssistantControl\":" + section + "}"))).Build();
        var secrets = new HomeAssistantReadToolsTests.RuntimeSecrets();
        using var host = new HostBuilder().ConfigureLogging(l => l.ClearProviders()).ConfigureServices(services =>
        {
            services.AddSingleton<ISecretProvider>(secrets);
            services.AddSingleton(TimeProvider.System);
            services.AddHomeAssistantControl(config);
        }).Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal(ControlPolicy.ConfigurationError, Assert.Single(error.Failures));
        Assert.Equal(0, secrets.Calls);
    }

    [Fact]
    public async Task AbsentControlDoesNotRequireHaOptionsAndPresentValidControlRequiresClient()
    {
        using var config = (ConfigurationRoot)new ConfigurationBuilder().Build();
        using var host = new HostBuilder().ConfigureLogging(l => l.ClearProviders()).ConfigureServices(services => services.AddHomeAssistantControl(config)).Build();
        await host.StartAsync();
        Assert.False(host.Services.GetRequiredService<ControlPolicy>().Enabled);
        await host.StopAsync();
        using var configured = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["HomeAssistantControl:Domains:light:AllowAllEntities"] = "true" }).Build();
        using var badHost = new HostBuilder().ConfigureLogging(l => l.ClearProviders()).ConfigureServices(services => services.AddHomeAssistantControl(configured)).Build();
        await Assert.ThrowsAsync<OptionsValidationException>(() => badHost.StartAsync());
    }
}
