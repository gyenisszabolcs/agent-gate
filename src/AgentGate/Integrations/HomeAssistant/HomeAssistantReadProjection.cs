using System.Text.Json;
using System.Text.RegularExpressions;
using AgentGate.Secrets;

namespace AgentGate.Integrations.HomeAssistant;

internal static class HomeAssistantReadProjection
{
    private static readonly string[] AttributeNames =
        ["friendly_name", "unit_of_measurement", "device_class", "state_class", "icon"];
    private static readonly string[] MediaTextNames = ["media_title", "media_artist", "media_content_type"];
    private static readonly string[] LocationNames =
        ["latitude", "longitude", "gps", "coordinate", "altitude", "elevation", "course", "speed", "radius"];

    private static bool LocationName(string text)
    {
        var normalized = new string(text.Where(char.IsAsciiLetterOrDigit).ToArray()).ToLowerInvariant();
        return LocationNames.Any(normalized.Contains);
    }

    // Konzervatív ismert alakok; nem általános szemantikus helyadat-felismerő.
    private static bool LocationText(string text) => LocationName(text) ||
        text.Contains("geo:", StringComparison.OrdinalIgnoreCase) ||
        text.Contains('°') && Regex.IsMatch(text, @"[0-9]\s*°", RegexOptions.CultureInvariant) ||
        Regex.IsMatch(text, @"[+-]?[0-9]{1,3}(?:\.[0-9]+)?\s*[,;/]\s*[+-]?[0-9]{1,3}(?:\.[0-9]+)?", RegexOptions.CultureInvariant) ||
        Regex.IsMatch(text, @"[+-]?[0-9]{1,3}\.[0-9]+\s+[+-]?[0-9]{1,3}\.[0-9]+|\b[NSEW]\s*[+-]?[0-9]|[0-9]\s*[NSEW]\b|\b(?:lat|lon|lng)\s*[:=]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsSafe(HomeAssistantState state) =>
        !LocationName(state.EntityId) && !LocationText(state.State) &&
        !AttributeNames.Any(name => state.Attributes.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            (LocationText(value.GetString()!) || name == "unit_of_measurement" &&
                value.GetString()!.Trim().ToLowerInvariant() is "°" or "deg" or "degree" or "degrees" or "rad"));

    public static bool IsSafe(HomeAssistantArea area) => !LocationText(area.AreaId) && !LocationText(area.AreaName);

    public static Dictionary<string, object> Config(JsonElement source)
    {
        var result = new Dictionary<string, object>();
        foreach (var name in new[] { "version", "time_zone", "language", "location_name" })
            if (source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !LocationText(value.GetString()!))
                result[name] = value.GetString()!;
        if (source.TryGetProperty("unit_system", out var units) && units.ValueKind == JsonValueKind.Object)
        {
            var projected = new Dictionary<string, string>();
            foreach (var name in new[] { "length", "mass", "temperature", "volume", "pressure", "wind_speed", "accumulated_precipitation" })
                if (units.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !LocationText(value.GetString()!))
                    projected[name] = value.GetString()!;
            result["unit_system"] = projected;
        }
        return result;
    }

    public static Dictionary<string, object> Attributes(HomeAssistantState state)
    {
        var source = state.Attributes;
        var result = new Dictionary<string, object>();
        foreach (var name in AttributeNames)
            if (source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !LocationText(value.GetString()!))
                result[name] = value.GetString()!;
        if (state.EntityId.StartsWith("media_player.", StringComparison.Ordinal))
        {
            foreach (var name in MediaTextNames)
                if (source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !LocationText(value.GetString()!))
                    result[name] = SecretRedactor.RedactText(value.GetString())!;
            if (source.TryGetProperty("volume_level", out var volume) && Control.ControlNumber.InRange(volume, 0, 1))
                result["volume_level"] = volume;
            if (source.TryGetProperty("is_volume_muted", out var muted) && muted.ValueKind is JsonValueKind.True or JsonValueKind.False)
                result["is_volume_muted"] = muted.GetBoolean();
        }
        return result;
    }

    public static Dictionary<string, object?> State(HomeAssistantState state)
    {
        var result = new Dictionary<string, object?> { ["entity_id"] = state.EntityId, ["state"] = state.State };
        foreach (var name in new[] { "friendly_name", "unit_of_measurement" })
            if (state.Attributes.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                result[name] = value.GetString();
        if (state.LastChanged is not null) result["last_changed"] = state.LastChanged;
        if (state.EntityId.StartsWith("media_player.", StringComparison.Ordinal)) result["attributes"] = Attributes(state);
        return result;
    }
}
