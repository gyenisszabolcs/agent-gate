using System.Text;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Policy;
using AgentGate.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantControlPolicyTests
{
    internal static ControlPolicy Policy(string? json = null) => ControlPolicy.FromConfiguration(new ConfigurationBuilder()
        .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json ?? """
        {"HomeAssistantControl":{"Domains":{
          "light":{"AllowAllEntities":true,"ControlServices":["turn_on","turn_off","toggle"]},
          "switch":{"EntityAllowlist":["switch.room"],"ControlServices":["turn_on","turn_off","toggle"]},
          "cover":{"AllowAllEntities":true,"ControlServices":["open_cover","close_cover","stop_cover","set_cover_position"],"SensitiveControlServices":["open_cover","close_cover","stop_cover"]},
          "climate":{"AllowAllEntities":true,"ControlServices":["set_temperature","set_hvac_mode","turn_on","turn_off"]},
          "scene":{"AllowAllEntities":true,"ControlServices":["turn_on"]},
          "input_boolean":{"AllowAllEntities":true,"ControlServices":["turn_on","turn_off","toggle"]},
          "lock":{"AllowAllEntities":true,"SensitiveControlServices":["lock","unlock"]}
        },"EntityDenylist":["light.denied"],"HvacModeAllowlist":["off","heat","cool","auto"],"RateLimit":{"PerEntityPerMinute":6,"GlobalPerMinute":60}}}
        """))).Build());

    internal static Dictionary<string, JsonElement> Arguments(string domain, string service, string? data = null, string? entity = null)
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["domain"] = JsonSerializer.SerializeToElement(domain),
            ["service"] = JsonSerializer.SerializeToElement(service),
            ["entity_id"] = JsonSerializer.SerializeToElement(entity ?? domain + ".room")
        };
        if (data is not null) args["data"] = JsonDocument.Parse(data).RootElement.Clone();
        return args;
    }

    public static IEnumerable<object[]> Matrix()
    {
        var operations = new (string Domain, RiskLevel Risk, string[] Services)[]
        {
            ("light", RiskLevel.Control, ["turn_on", "turn_off", "toggle"]),
            ("switch", RiskLevel.Control, ["turn_on", "turn_off", "toggle"]),
            ("input_boolean", RiskLevel.Control, ["turn_on", "turn_off", "toggle"]),
            ("cover", RiskLevel.Control, ["open_cover", "close_cover", "stop_cover", "set_cover_position"]),
            ("climate", RiskLevel.Control, ["set_temperature", "set_hvac_mode", "turn_on", "turn_off"]),
            ("scene", RiskLevel.Control, ["turn_on"]),
            ("cover", RiskLevel.SensitiveControl, ["open_cover", "close_cover", "stop_cover"]),
            ("lock", RiskLevel.SensitiveControl, ["lock", "unlock"])
        };
        foreach (var operation in operations)
            foreach (var service in operation.Services)
                yield return new object[] { operation.Domain, service, operation.Risk, service switch
                {
                    "set_cover_position" => "{\"position\":50}", "set_temperature" => "{\"temperature\":20}",
                    "set_hvac_mode" => "{\"hvac_mode\":\"heat\"}", _ => "{}"
                } };
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void EveryMatrixOperationUsesItsExactRisk(string domain, string service, RiskLevel risk, string data)
    {
        var policy = Policy();
        Assert.Equal(service, policy.Validate(Arguments(domain, service, data), risk).Service);
        if (domain != "cover" || service == "set_cover_position")
            Assert.Throws<GateToolException>(() => policy.Validate(Arguments(domain, service, data), risk == RiskLevel.Control ? RiskLevel.SensitiveControl : RiskLevel.Control));
    }

    [Theory]
    [InlineData("light", "turn_on", "{\"brightness_pct\":0}")]
    [InlineData("light", "toggle", "{\"brightness_pct\":100,\"color_temp_kelvin\":6500}")]
    [InlineData("light", "turn_on", "{\"color_temp_kelvin\":2000}")]
    [InlineData("cover", "set_cover_position", "{\"position\":0}")]
    [InlineData("cover", "set_cover_position", "{\"position\":100}")]
    [InlineData("climate", "set_temperature", "{\"temperature\":10}")]
    [InlineData("climate", "set_temperature", "{\"temperature\":30}")]
    [InlineData("climate", "set_hvac_mode", "{\"hvac_mode\":\"off\"}")]
    public void AcceptsInclusiveTypedBoundaries(string domain, string service, string data) => Policy().Validate(Arguments(domain, service, data), RiskLevel.Control);

    [Theory]
    [InlineData("-1e-100")]
    [InlineData("100.000000000000000000000000000001")]
    public void NumericBoundsMustNotRoundInvalidCallerValuesIntoRange(string value) =>
        Assert.Throws<GateToolException>(() => Policy().Validate(Arguments("light", "turn_on", "{\"brightness_pct\":" + value + "}"), RiskLevel.Control));

    [Fact]
    public void ValidJsonNumberIsPreservedWithoutRounding()
    {
        const string number = "0.12345678901234567890123456789";
        var request = Policy().Validate(Arguments("light", "turn_on", "{\"brightness_pct\":" + number + "}"), RiskLevel.Control);
        Assert.Equal(number, request.Data.GetProperty("brightness_pct").GetRawText());
    }

    [Fact]
    public void AllowsOnlyConfiguredCanonicalSingleTargetAndData()
    {
        var policy = Policy();
        var request = policy.Validate(Arguments("light", "turn_on", "{\"brightness_pct\":50,\"color_temp_kelvin\":3000}"), RiskLevel.Control);
        Assert.Equal("light.room", request.EntityId);
        Assert.Equal("{\"brightness_pct\":50,\"color_temp_kelvin\":3000}", request.Data.GetRawText());
        Assert.Throws<GateToolException>(() => policy.Validate(Arguments("light", "turn_on", entity: "light.denied"), RiskLevel.Control));
    }
}
