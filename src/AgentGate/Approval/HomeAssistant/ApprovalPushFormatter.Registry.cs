using System.Text;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Integrations.HomeAssistant.Registry;

namespace AgentGate.Approval.HomeAssistant;

public sealed partial class ApprovalPushFormatter
{
    private static void ValidateRegistrySummary(string summary, ApprovalPresentation display, string token)
    {
        try
        {
            if (summary.Length > 32_768) throw InvalidSummary();
            using var document = JsonDocument.Parse(summary);
            var root = document.RootElement;
            RegistrySocket.ValidateUnique(root);
            var deviceTarget = root.TryGetProperty("device_id", out var deviceId);
            var targetField = deviceTarget ? "device_id" : "entity_id";
            if (root.EnumerateObject().Count() != 5 || root.EnumerateObject().Any(field =>
                field.Name != targetField && field.Name is not ("patch" or "before" or "after" or "changes"))) throw InvalidSummary();
            var target = Text(root, targetField);
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
            if (Encoding.UTF8.GetByteCount(details) > 2800) throw InvalidSummary();

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
}
