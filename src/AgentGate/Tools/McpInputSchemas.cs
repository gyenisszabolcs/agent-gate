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
          "description": "Csak a megadott mezők változnak. Eszköz (device_id): kizárólag area_id, name_by_user, labels (teljes címkelista). Entitás (entity_id): area_id, labels_add, labels_remove, name, icon, hidden_by, disabled_by. Az explicit null visszaállítás; a gate a céltípushoz nem tartozó mezőt elutasítja, és policy-/előállapot-ellenőrzést végez.",
          "minProperties": 1,
          "properties": {
            "area_id": { "type": ["string", "null"], "minLength": 1, "maxLength": 64, "pattern": "^[a-z0-9_-]+$" },
            "name_by_user": { "type": ["string", "null"], "minLength": 1, "maxLength": 80 },
            "labels": { "type": "array", "maxItems": 16, "uniqueItems": true, "items": { "type": "string", "minLength": 1, "maxLength": 64, "pattern": "^[a-z0-9_-]+$" } },
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

    private static readonly JsonElement CreateLabel = JsonSerializer.SerializeToElement(JsonNode.Parse("""
        {
          "type": "object", "required": ["name"], "additionalProperties": { "not": {} },
          "description": "Új címke; nincs címkemódosítás vagy -törlés. A névütközés tiltott; Configuration, gate-jóváhagyással.",
          "properties": {
            "name": { "type": "string", "minLength": 1, "maxLength": 80 },
            "icon": { "type": ["string", "null"], "pattern": "^mdi:[a-z0-9-]+$", "maxLength": 64 },
            "color": { "type": ["string", "null"], "description": "HA témaszín (például blue) vagy #RRGGBB; tetszőleges CSS tiltott." }
          }
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
            "gate_approval_status" => "wait_seconds",
            _ => null
        };
        if (parameter is null) return tool;
        // A megosztott SDK-eszközdefiníciót nem módosítjuk. A többi protokollmetaadat megmarad.
        var copy = JsonSerializer.Deserialize<Tool>(JsonSerializer.Serialize(tool, McpJsonUtilities.DefaultOptions), McpJsonUtilities.DefaultOptions)!;
        var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!;
        schema["properties"]![parameter] = parameter == "wait_seconds"
            ? JsonNode.Parse("""{"type":"integer","default":0,"minimum":0,"maximum":120,"description":"Jóváhagyáskérés után ennyi másodpercig várjon döntésre vagy eredményre, majd a döntés szerint folytassa. 0: azonnali állapot. A jóváhagyás nem végrehajtási siker: szükség esetén az eredményt is várja meg. Időtúllépés után státuszt kérdezzen újra, ne a műveletet ismételje."}""")
            : JsonNode.Parse((parameter == "patch" ? Patch : ControlData).GetRawText());
        if (tool.Name == "ha_update_registry")
        {
            schema["properties"]!["create_label"] = JsonNode.Parse(CreateLabel.GetRawText());
            var batchPatch = JsonNode.Parse(Patch.GetRawText())!;
            batchPatch["properties"]!["labels_add_created"] = JsonNode.Parse("""
                {"type":"array","minItems":1,"maxItems":16,"uniqueItems":true,"items":{"type":"integer","minimum":0,"maximum":49},"description":"Korábbi create_label tag indexe a teljes, 0-tól számozott items listában, nem a létrehozások sorszáma vagy név/ID. A 0. és 2. helyen létrehozott címkék hivatkozása [0] és [2]. Entitásnál hozzáadás; eszköznél a labels teljes listáját egészíti ki."}
                """);
            var member = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["entity_id"] = schema["properties"]!["entity_id"]!.DeepClone(),
                    ["device_id"] = schema["properties"]!["device_id"]!.DeepClone(),
                    ["patch"] = batchPatch,
                    ["create_label"] = JsonNode.Parse(CreateLabel.GetRawText())
                },
                ["oneOf"] = JsonNode.Parse("""
                    [
                      {"required":["entity_id","patch"],"not":{"anyOf":[{"required":["device_id"]},{"required":["create_label"]}]}},
                      {"required":["device_id","patch"],"not":{"anyOf":[{"required":["entity_id"]},{"required":["create_label"]}]}},
                      {"required":["create_label"],"not":{"anyOf":[{"required":["entity_id"]},{"required":["device_id"]},{"required":["patch"]}]}}
                    ]
                    """),
                ["additionalProperties"] = JsonNode.Parse("""{"not":{}}""")
            };
            schema["properties"]!["items"] = new JsonObject
            {
                ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 50, ["items"] = member,
                ["description"] = "Rögzített sorrendű, legfeljebb 50 entitás/eszköz/create_label tag. Kizárólag items vagy egycélú mód. Egy közös jóváhagyás, teljes HA értesítés, első hibánál stop; nincs rollback/retry. Azonos cél csak egyszer szerepelhet."
            };
            schema["oneOf"] = JsonNode.Parse("""
                [
                  {"required":["entity_id","patch"],"not":{"anyOf":[{"required":["device_id"]},{"required":["create_label"]},{"required":["items"]}]}},
                  {"required":["device_id","patch"],"not":{"anyOf":[{"required":["entity_id"]},{"required":["create_label"]},{"required":["items"]}]}},
                  {"required":["create_label"],"not":{"anyOf":[{"required":["entity_id"]},{"required":["device_id"]},{"required":["patch"]},{"required":["items"]}]}},
                  {"required":["items"],"not":{"anyOf":[{"required":["entity_id"]},{"required":["device_id"]},{"required":["patch"]},{"required":["create_label"]}]}}
                ]
                """);
            // A célkizárásokat a nyers bemeneten a RegistryPolicy is kötelezően ellenőrzi.
            schema["required"] = new JsonArray();
        }
        copy.InputSchema = JsonSerializer.SerializeToElement(schema);
        return copy;
    }
}
