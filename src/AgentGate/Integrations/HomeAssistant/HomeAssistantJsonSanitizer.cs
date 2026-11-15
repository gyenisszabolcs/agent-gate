using System.Text.Json;

namespace AgentGate.Integrations.HomeAssistant;

internal static class HomeAssistantJsonSanitizer
{
    private const string Redacted = "[SZŰRVE]";
    private static readonly string[] SensitiveNames =
        ["token", "secret", "password", "passwd", "authorization", "apikey", "credential", "cookie", "privatekey", "entitypicture"];

    public static JsonElement Sanitize(JsonElement source, string bearerToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, source, bearerToken);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, string token)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    var normalized = new string(property.Name.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
                    if (SensitiveNames.Any(normalized.Contains) || property.Name.Contains(token, StringComparison.Ordinal))
                        continue;
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value, token);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item, token);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var text = element.GetString()!;
                // Konzervatív: minden query-, fragment- vagy userinfo-gyanús szöveget egészben takarunk.
                writer.WriteStringValue(text.IndexOfAny(['?', '#', '@']) >= 0
                    ? Redacted : text.Replace(token, Redacted, StringComparison.Ordinal));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
