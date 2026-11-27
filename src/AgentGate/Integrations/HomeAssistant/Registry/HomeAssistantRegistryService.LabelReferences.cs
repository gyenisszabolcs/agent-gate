using System.Text.Json;
using System.Text.Json.Nodes;
using AgentGate.Approval;

namespace AgentGate.Integrations.HomeAssistant.Registry;

public sealed partial class HomeAssistantRegistryService
{
    private sealed record LabelBinding(int Position, string CreateHash, string Placeholder);
    private sealed record CreatedBatchLabel(PreparedOperation Operation, JsonElement Result);

    // Csak a rögzített korábbi create validált válaszát helyettesítjük. Név szerinti feloldás nincs.
    private PreparedOperation ResolveBatchLabels(PreparedOperation member, IReadOnlyDictionary<int, CreatedBatchLabel> created)
    {
        if (!member.Parameters.ContainsKey("label_catalog"))
        {
            if (member.Parameters.ContainsKey("label_refs")) throw RegistryPolicy.Invalid();
            return member;
        }
        var isCreate = member.Parameters.ContainsKey("create_label");
        var refs = isCreate ? [] : JsonSerializer.Deserialize<LabelBinding[]>(member.Parameters["label_refs"]!) ?? throw RegistryPolicy.Invalid();
        if (refs.Select(reference => reference.Position).Distinct().Count() != refs.Length) throw RegistryPolicy.Invalid();
        foreach (var reference in refs)
            if (!created.TryGetValue(reference.Position, out var source) || reference.CreateHash != source.Operation.ComputeHash("") ||
                reference.Placeholder != LabelCreation.Placeholder(reference.Position)) throw RegistryPolicy.Invalid();
        if (!isCreate)
        {
            using var patch = JsonDocument.Parse(member.Parameters["patch"]!);
            var pending = patch.RootElement.EnumerateObject().Where(p => p.Name is "labels" or "labels_add")
                .SelectMany(p => RegistryPolicy.Ids(p.Value)).Where(id => id.StartsWith("gate_pending_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
            if (!pending.SetEquals(refs.Select(reference => reference.Placeholder))) throw RegistryPolicy.Invalid();
        }
        var result = member.Parameters.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var field in new[] { "patch", "before", "after", "changes", "label_catalog" })
        {
            if (!result.TryGetValue(field, out var json)) continue;
            var node = JsonNode.Parse(json!);
            Translate(node);
            // A meglévő végrehajtó kanonikus JSON-t hasonlít. A csomag hash-ét nem számoljuk újra.
            result[field] = node is null ? "null" : JsonSerializer.Serialize(node);
            if (field == "label_catalog") result[field] = LabelCreation.Catalog(JsonSerializer.Deserialize<JsonElement>(result[field]!)).GetRawText();
        }
        result.Remove("label_refs");
        if (!isCreate) result.Remove("label_catalog");
        return new(member.Tool, member.Risk, result, requiresLosslessAudit: true);

        void Translate(JsonNode? node)
        {
            if (node is JsonArray rows)
            {
                foreach (var row in rows) Translate(row);
                return;
            }
            if (node is not JsonObject obj) return;
            foreach (var property in obj.ToArray())
            {
                if (property.Key == "label_id" && property.Value is JsonValue id) obj[property.Key] = Resolve(id.GetValue<string>());
                else if (property.Key is "labels" or "labels_add" or "labels_remove" && property.Value is JsonArray labels)
                {
                    var resolved = labels.Select(label => Resolve(label!.GetValue<string>())).ToArray();
                    // A szerver ID-ja más helyre rendeződhet, mint a jóváhagyott helyőrző.
                    obj[property.Key] = JsonSerializer.SerializeToNode(RegistryPolicy.Ids(JsonSerializer.SerializeToElement(resolved)));
                }
                else Translate(property.Value);
            }
        }
        string Resolve(string id)
        {
            if (!id.StartsWith("gate_pending_", StringComparison.Ordinal)) return id;
            var source = created.SingleOrDefault(pair => LabelCreation.Placeholder(pair.Key) == id);
            if (source.Value is null) throw RegistryPolicy.Invalid();
            return source.Value.Result.GetProperty("label_id").GetString()!;
        }
    }

    private async Task<JsonElement?> ExecuteBatchMemberAsync(PreparedOperation member, IReadOnlyDictionary<int, CreatedBatchLabel> created, CancellationToken ct)
    {
        var resolved = ResolveBatchLabels(member, created);
        if (resolved.Parameters.ContainsKey("create_label")) return await ExecuteCreateAsync(resolved, ct);
        if (!member.Parameters.TryGetValue("label_catalog", out var catalog))
        {
            await ExecuteCoreAsync(resolved, ct);
            return null;
        }
        // A katalógusban kizárólag az igazolt saját create-k helyőrzői változhatnak ID-vá.
        var projection = new PreparedOperation(member.Tool, member.Risk, new Dictionary<string, string?>
        {
            ["create_label"] = "{}", ["label_catalog"] = catalog
        }, requiresLosslessAudit: true);
        var expected = LabelCreation.Catalog(JsonSerializer.Deserialize<JsonElement>(ResolveBatchLabels(projection, created).Parameters["label_catalog"]!));
        async Task CheckCatalog(CancellationToken token)
        {
            var current = LabelCreation.Catalog(await client.ReadRegistryLabelsAsync(true, token));
            if (current.GetRawText() != expected.GetRawText()) throw new RegistryPrestateChangedException();
        }
        await CheckCatalog(ct);
        await ExecuteCoreAsync(resolved, ct, CheckCatalog);
        return null;
    }
}
