using System.Text;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.Registry;

namespace AgentGate.Approval.HomeAssistant;

public sealed partial class ApprovalPushFormatter
{
    public string RegistryBatchDetails(ApprovalNotice request, string token)
    {
        try
        {
            if (request.Tool != "ha_update_registry" || request.Presentation?.Items is not { Count: >= 1 and <= 50 } displays ||
                request.Summary.Length > 2_097_152) throw InvalidSummary();
            using var document = JsonDocument.Parse(request.Summary);
            RegistrySocket.ValidateUnique(document.RootElement);
            if (document.RootElement.EnumerateObject().Count() != 1) throw InvalidSummary();
            using var members = JsonDocument.Parse(Text(document.RootElement, "items"));
            RegistrySocket.ValidateUnique(members.RootElement);
            if (members.RootElement.ValueKind != JsonValueKind.Array || members.RootElement.GetArrayLength() != displays.Count) throw InvalidSummary();
            var lines = new List<string> { $"{displays.Count} elem módosítása · Mikaela kéri", "Változás (előtte → utána):" };
            var index = 0;
            foreach (var member in members.RootElement.EnumerateArray())
            {
                var display = displays[index++];
                ValidateRegistrySummary(member.GetRawText(), display, token, extended: true);
                var target = member.TryGetProperty("device_id", out var device) ? device.GetString()! : Text(member, "entity_id");
                lines.Add($"{index}. {display.RegistryLine(token, extended: true)} ({target})");
            }
            lines.Add("Rögzített sorrend; az első hibánál leáll. Nincs visszagörgetés vagy újrapróbálás. Írás előtt újraellenőrzés; nem atomi védelem.");
            var details = string.Join('\n', lines);
            if (Encoding.UTF8.GetByteCount(details) > 32_768 || token.Length > 0 && details.Contains(token, StringComparison.Ordinal)) throw InvalidSummary();
            return details;
        }
        catch (Exception) { throw InvalidSummary(); }
    }
}
