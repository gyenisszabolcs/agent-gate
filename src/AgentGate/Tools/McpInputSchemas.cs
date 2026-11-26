using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AgentGate.Tools;

// Csak a publikált leírás változik; a nyers argumentumokat továbbra is az adott policy ellenőrzi.
internal static class McpInputSchemas
{
    private static readonly JsonElement Patch = JsonSerializer.SerializeToElement(JsonNode.Parse("""
        {
          "type": "object",
          "description": "Csak a megadott mezők változnak; az explicit null visszaállítás. A gate további policy- és előállapot-ellenőrzést végez.",
          "minProperties": 1,
          "properties": {
            "area_id": { "type": ["string", "null"], "minLength": 1, "maxLength": 64, "pattern": "^[a-z0-9_-]+$" },
            "labels_add": { "type": "array", "maxItems": 16, "uniqueItems": true, "items": { "type": "string", "minLength": 1, "maxLength": 64, "pattern": "^[a-z0-9_-]+$" } },
            "labels_remove": { "type": "array", "maxItems": 16, "uniqueItems": true, "items": { "type": "string", "minLength": 1, "maxLength": 64, "pattern": "^[a-z0-9_-]+$" } },
            "name": { "type": ["string", "null"], "minLength": 1, "maxLength": 80 },
            "icon": { "type": ["string", "null"], "minLength": 1, "maxLength": 64, "pattern": "^mdi:[a-z0-9-]+$" },
            "hidden_by": { "type": ["string", "null"], "enum": ["user", null] },
            "disabled_by": { "type": ["string", "null"], "enum": ["user", null] }
          },
          "additionalProperties": { "not": {} }
        }
        """));

    private static readonly JsonElement ControlData = JsonSerializer.SerializeToElement(JsonNode.Parse("""
        {
          "type": ["object", "null"],
          "default": null,
          "description": "A domain és service által engedélyezett mezők. A gate a szolgáltatáshoz nem tartozó mezőt elutasítja; a konfiguráció további korlátokat szabhat.",
          "properties": {
            "brightness_pct": { "type": "number", "minimum": 0, "maximum": 100 },
            "color_temp_kelvin": { "type": "integer", "minimum": 2000, "maximum": 6500 },
            "position": { "type": "integer", "minimum": 0, "maximum": 100 },
            "temperature": { "type": "number", "minimum": 10, "maximum": 30 },
            "hvac_mode": { "type": "string" },
            "volume_level": { "type": "number", "minimum": 0, "maximum": 1 },
            "is_volume_muted": { "type": "boolean" },
            "message": { "type": "string" }
          },
          "additionalProperties": { "not": {} }
        }
        """));

    public static Tool Describe(Tool tool)
    {
        var parameter = tool.Name switch
        {
            "ha_update_registry" => "patch",
            "ha_control" or "ha_sensitive_control" => "data",
            _ => null
        };
        if (parameter is null) return tool;
        // A megosztott SDK-eszközdefiníciót nem módosítjuk. A többi protokollmetaadat megmarad.
        var copy = JsonSerializer.Deserialize<Tool>(JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions), McpJsonUtilities.DefaultOptions)!;
        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!;
        schema["properties"]![parameter] = JsonNode.Parse((parameter == "patch" ? Patch : ControlData).GetRawText());
        copy.InputSchema = JsonSerializer.SerializeToElement(schema);
        return copy;
    }
}
