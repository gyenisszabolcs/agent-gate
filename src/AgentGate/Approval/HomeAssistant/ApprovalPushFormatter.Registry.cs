using System.Text;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Integrations.HomeAssistant.Registry;

namespace AgentGate.Approval.HomeAssistant;

public sealed partial class ApprovalPushFormatter
{
    private static void ValidateRegistrySummary(string summary, ApprovalPresentation display, string token, bool extended = false)
    {
        try
        {
            if (summary.Length > 32_768) throw InvalidSummary();
            using var document = JsonDocument.Parse(summary);
            var root = document.RootElement;
            RegistrySocket.ValidateUnique(root);
            if (root.TryGetProperty("create_label", out _))
            {
                ValidateLabelCreation(root, display, token);
                return;
            }
            var deviceTarget = root.TryGetProperty("device_id", out var deviceId);
            var targetField = deviceTarget ? "device_id" : "entity_id";
            var hasReferences = extended && root.TryGetProperty("label_refs", out _);
            if (root.EnumerateObject().Count() != (hasReferences ? 7 : 5) || root.EnumerateObject().Any(field =>
                field.Name != targetField && field.Name is not ("patch" or "before" or "after" or "changes") &&
                !(hasReferences && field.Name is "label_refs" or "label_catalog"))) throw InvalidSummary();
            var target = Text(root, targetField);
            if (display.EntityIdFallback is { } fallback && (deviceTarget || fallback != target)) throw InvalidSummary();
            if (deviceTarget) _ = RegistryPolicy.Id(deviceId);
            else if (!ControlPolicy.CanonicalEntity(target)) throw InvalidSummary();
            using var before = JsonDocument.Parse(Text(root, "before"));
            using var after = JsonDocument.Parse(Text(root, "after"));
            using var changes = JsonDocument.Parse(Text(root, "changes"));
            RegistrySocket.ValidateUnique(before.RootElement);
            RegistrySocket.ValidateUnique(after.RootElement);
            RegistrySocket.ValidateUnique(changes.RootElement);
            if (!changes.RootElement.EnumerateObject().Select(field => field.Name).ToHashSet(StringComparer.Ordinal).SetEquals(display.Changes.Keys))
                throw InvalidSummary();
            var lines = new List<string> { $"Cél: {target}", "Változás (előtte → utána):" };
            foreach (var field in changes.RootElement.EnumerateObject().OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                if (deviceTarget ? field.Name is not ("area_id" or "labels" or "name_by_user") :
                    field.Name is not ("area_id" or "labels" or "name" or "icon" or "hidden_by" or "disabled_by")) throw InvalidSummary();
                var oldValue = before.RootElement.GetProperty(field.Name);
                var newValue = after.RootElement.GetProperty(field.Name);
                if (newValue.GetRawText() != field.Value.GetRawText()) throw InvalidSummary();
                lines.Add($"{field.Name}: {Display(oldValue)} → {Display(newValue)}");
            }
            if (lines.Count == 2) throw InvalidSummary();
            lines.Add("Írás előtt újraellenőrzés; nem atomi védelem.");
            var details = string.Join('\n', lines);
            if (Encoding.UTF8.GetByteCount(details) > (extended ? 32_768 : 2800)) throw InvalidSummary();

            string Display(JsonElement value)
            {
                var text = JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                // Nincs helyettesítés, levágás vagy többsoros szöveg: hiányos diff nem hagyható jóvá.
                if (SafeLine(text, 8192, token) != text) throw InvalidSummary();
                return text;
            }
        }
        catch (Exception) { throw InvalidSummary(); }
    }

    private static void ValidateLabelCreation(JsonElement root, ApprovalPresentation display, string token)
    {
        if (root.EnumerateObject().Count() != 5 || root.EnumerateObject().Any(p => p.Name is not ("create_label" or "before" or "after" or "changes" or "label_catalog")) || Text(root, "before") != "null") throw InvalidSummary();
        using var intent = JsonDocument.Parse(Text(root, "create_label"));
        var expected = LabelCreation.Intent(intent.RootElement);
        var canonical = JsonSerializer.Serialize(expected);
        if (Text(root, "create_label") != canonical || Text(root, "after") != canonical || Text(root, "changes") != canonical ||
            display.TargetName != (string)expected["name"]!) throw InvalidSummary();
        using var catalog = JsonDocument.Parse(Text(root, "label_catalog"));
        _ = LabelCreation.Catalog(catalog.RootElement);
        LabelCreation.RequireAbsent(catalog.RootElement, display.TargetName!);
        foreach (var field in new[] { "icon", "color" })
        {
            if (expected[field] is string value)
            {
                if (!display.Changes.TryGetValue(field, out var change) || change.After != value) throw InvalidSummary();
            }
            else if (display.Changes.ContainsKey(field)) throw InvalidSummary();
        }
        _ = LabelCreation.Display(display, token);
    }
}
