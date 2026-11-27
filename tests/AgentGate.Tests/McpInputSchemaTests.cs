using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentGate.Tests;

public sealed class McpInputSchemaTests
{
    [Fact]
    public async Task EveryPublishedInputSchemaUsesObjectsInsteadOfBooleanSchemas()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var app = new McpTestApplication(token);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { }
        }, timeout.Token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = body.Split('\n').Last(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        using var document = JsonDocument.Parse(body);
        var tools = document.RootElement.GetProperty("result").GetProperty("tools");
        Assert.NotEmpty(tools.EnumerateArray());
        var registered = app.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        Assert.Equal(registered.Count, tools.GetArrayLength());
        foreach (var original in registered)
        {
            var published = Assert.Single(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() == original.ProtocolTool.Name);
            var expected = JsonSerializer.SerializeToNode(original.ProtocolTool, McpJsonUtilities.DefaultOptions)!.AsObject();
            var actual = JsonNode.Parse(published.GetRawText())!.AsObject();
            var parameter = original.ProtocolTool.Name switch
            {
                "ha_update_registry" => "patch",
                "ha_control" or "ha_sensitive_control" => "data",
                "gate_approval_status" => "wait_seconds",
                _ => null
            };
            if (parameter is not null)
            {
                expected["inputSchema"]!["properties"]!.AsObject().Remove(parameter);
                actual["inputSchema"]!["properties"]!.AsObject().Remove(parameter);
            }
            if (original.ProtocolTool.Name == "ha_update_registry")
            {
                // A patch/items alternatíva is publikációs kiegészítés; az SDK-példányt nem írja át.
                expected["inputSchema"]!["properties"]!.AsObject().Remove("items");
                actual["inputSchema"]!["properties"]!.AsObject().Remove("items");
                expected["inputSchema"]!.AsObject().Remove("required");
                actual["inputSchema"]!.AsObject().Remove("required");
            }
            Assert.True(JsonNode.DeepEquals(expected, actual), original.ProtocolTool.Name);
        }
        Assert.All(tools.EnumerateArray(), tool =>
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString()));
        var invalid = tools.EnumerateArray().SelectMany(tool => BooleanSchemaPaths(
            tool.GetProperty("inputSchema"), tool.GetProperty("name").GetString()!)).ToArray();
        Assert.True(invalid.Length == 0, string.Join("\n", invalid));

        var registry = Assert.Single(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() == "ha_update_registry");
        var description = registry.GetProperty("description").GetString()!;
        Assert.Contains("azonosítót, nem területnevet", description);
        Assert.Contains("Öröklött before állapotot ne küldj", description);
        Assert.Contains("a gate maga olvassa", description);
        Assert.Contains("patch.area_id:null", description);
        Assert.Contains("hiányzó mező változatlan", description);
        Assert.Contains("device_entities(device_id)", description);
        Assert.Contains("összes aktív", description);
        Assert.Contains("összes entitása ugyanabba a területbe", description);
        Assert.Contains("az eszköz területét", description);
        Assert.Contains("eltérő saját entitásterületet", description);
        Assert.Contains("Ha csak egy entitás", description);
        Assert.Contains("eszköz nélküli entitás", description);
        Assert.Contains("bizonytalan esetben kérdezz", description);
        Assert.Contains("előbb az eszközmódosítás sikerét", description);
        Assert.DoesNotContain("továbbra is tiltott", description);
        Assert.Contains("minden tag külön policy-ellenőrzést", description);
        Assert.Contains("Egy közös, egyszer használható, lejáró jóváhagyás", description);
        Assert.Contains("nincs rollback/retry", description);
        var exampleStart = description.IndexOf('{');
        var exampleEnd = description.IndexOf("}}", exampleStart, StringComparison.Ordinal) + 2;
        using var example = JsonDocument.Parse(description[exampleStart..exampleEnd]);
        Assert.Equal("light.yeelight_nappali", example.RootElement.GetProperty("entity_id").GetString());
        Assert.Equal("nappali", example.RootElement.GetProperty("patch").GetProperty("area_id").GetString());
        Assert.Equal(2, example.RootElement.EnumerateObject().Count());
        var input = registry.GetProperty("inputSchema");
        Assert.False(input.GetProperty("properties").TryGetProperty("before", out _));
        Assert.Empty(input.GetProperty("required").EnumerateArray());
        var items = input.GetProperty("properties").GetProperty("items");
        Assert.Equal("array", items.GetProperty("type").GetString());
        Assert.Equal(1, items.GetProperty("minItems").GetInt32());
        Assert.Equal(50, items.GetProperty("maxItems").GetInt32());
        var member = items.GetProperty("items");
        Assert.Equal("object", member.GetProperty("type").GetString());
        Assert.Equal("patch", Assert.Single(member.GetProperty("required").EnumerateArray()).GetString());
        Assert.Empty(member.GetProperty("additionalProperties").GetProperty("not").EnumerateObject());
        Assert.Equal(new[] { "device_id", "entity_id", "patch" }, member.GetProperty("properties").EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        var patch = input.GetProperty("properties").GetProperty("patch");
        Assert.Equal("object", patch.GetProperty("type").GetString());
        Assert.Equal(1, patch.GetProperty("minProperties").GetInt32());
        // A false helyett ekvivalens, minden értéket kizáró objektumséma zárja az ismeretlen mezőket.
        Assert.Empty(patch.GetProperty("additionalProperties").GetProperty("not").EnumerateObject());
        var fields = patch.GetProperty("properties");
        Assert.Equal(new[] { "area_id", "disabled_by", "hidden_by", "icon", "labels", "labels_add", "labels_remove", "name", "name_by_user" },
            fields.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "area_id", "name", "name_by_user", "icon", "hidden_by", "disabled_by" })
            Assert.Equal(new[] { "string", "null" }, fields.GetProperty(name).GetProperty("type").EnumerateArray().Select(type => type.GetString()));
        foreach (var name in new[] { "labels", "labels_add", "labels_remove" })
        {
            var field = fields.GetProperty(name);
            Assert.Equal("array", field.GetProperty("type").GetString());
            Assert.Equal("string", field.GetProperty("items").GetProperty("type").GetString());
            Assert.Equal(16, field.GetProperty("maxItems").GetInt32());
            Assert.True(field.GetProperty("uniqueItems").GetBoolean());
        }
        Assert.False(patch.TryGetProperty("required", out _));
        Assert.All(fields.EnumerateObject(), field => Assert.False(field.Value.TryGetProperty("default", out _)));

        foreach (var name in new[] { "ha_control", "ha_sensitive_control" })
        {
            var control = Assert.Single(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() == name);
            var data = control.GetProperty("inputSchema").GetProperty("properties").GetProperty("data");
            Assert.Equal(new[] { "object", "null" }, data.GetProperty("type").EnumerateArray().Select(type => type.GetString()));
            Assert.Equal("boolean", data.GetProperty("properties").GetProperty("is_volume_muted").GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("default").ValueKind);
            Assert.Empty(data.GetProperty("additionalProperties").GetProperty("not").EnumerateObject());
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{\"properties\":{\"patch\":true}}")]
    [InlineData("{\"$defs\":{\"value\":false}}")]
    [InlineData("{\"definitions\":{\"value\":true}}")]
    [InlineData("{\"items\":false}")]
    [InlineData("{\"items\":[true]}")]
    [InlineData("{\"prefixItems\":[false]}")]
    [InlineData("{\"anyOf\":[true]}")]
    [InlineData("{\"allOf\":[false]}")]
    [InlineData("{\"oneOf\":[true]}")]
    [InlineData("{\"not\":false}")]
    [InlineData("{\"additionalProperties\":false}")]
    [InlineData("{\"unevaluatedProperties\":true}")]
    [InlineData("{\"patternProperties\":{\"x\":false}}")]
    [InlineData("{\"dependentSchemas\":{\"x\":true}}")]
    [InlineData("{\"dependencies\":{\"x\":false}}")]
    [InlineData("{\"contains\":true}")]
    [InlineData("{\"propertyNames\":false}")]
    [InlineData("{\"if\":true,\"then\":{},\"else\":{}}")]
    [InlineData("{\"additionalItems\":false}")]
    [InlineData("{\"unevaluatedItems\":true}")]
    [InlineData("{\"contentSchema\":false}")]
    public void WalkerFindsBooleanSchemas(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Single(BooleanSchemaPaths(document.RootElement));
    }

    [Fact]
    public void WalkerAllowsBooleanDataAndBooleanTypedParameters()
    {
        using var document = JsonDocument.Parse("""
            {"type":"object","properties":{"flag":{"type":"boolean","default":false,"enum":[true,false],"const":true}},
             "examples":[{"flag":true}],"default":{"flag":false},"dependencies":{"flag":["other"]}}
            """);
        Assert.Empty(BooleanSchemaPaths(document.RootElement));
    }

    private static IEnumerable<string> BooleanSchemaPaths(JsonElement schema, string path = "$")
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            yield return path + ": " + schema.GetRawText();
            yield break;
        }
        if (schema.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in schema.EnumerateObject())
        {
            var childPath = path + "." + property.Name;
            if (property.Name is "properties" or "patternProperties" or "$defs" or "definitions" or "dependentSchemas" or "dependencies")
            {
                foreach (var child in property.Value.EnumerateObject())
                    if (child.Value.ValueKind != JsonValueKind.Array)
                        foreach (var match in BooleanSchemaPaths(child.Value, childPath + "." + child.Name)) yield return match;
            }
            else if (property.Name is "allOf" or "anyOf" or "oneOf" or "prefixItems" ||
                     property.Name == "items" && property.Value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var child in property.Value.EnumerateArray())
                {
                    foreach (var match in BooleanSchemaPaths(child, childPath + "[" + index + "]")) yield return match;
                    index++;
                }
            }
            else if (property.Name is "items" or "additionalProperties" or "unevaluatedProperties" or
                     "additionalItems" or "unevaluatedItems" or "contains" or "propertyNames" or
                     "not" or "if" or "then" or "else" or "contentSchema")
                foreach (var match in BooleanSchemaPaths(property.Value, childPath)) yield return match;
        }
    }
}
