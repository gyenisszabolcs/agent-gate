using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace AgentGate.Integrations.HomeAssistant.Control;

/// <summary>Pontos decimális összehasonlítás; a JSON-számot nem kerekítjük lebegőpontossá.</summary>
internal static class ControlNumber
{
    // Fix lexikális erőforráskorlát, még a double/BigInteger feldolgozás előtt; nem konfigurálható.
    private const int MaximumNumberTokenLength = 4096;
    public static bool IsFinite(JsonElement value) => value.ValueKind == JsonValueKind.Number &&
        value.GetRawText().Length <= MaximumNumberTokenLength &&
        value.TryGetDouble(out var number) && double.IsFinite(number);

    public static bool InRange(JsonElement value, int min, int max) => IsFinite(value) &&
        Compare(value, JsonSerializer.SerializeToElement(min)) >= 0 && Compare(value, JsonSerializer.SerializeToElement(max)) <= 0;

    public static int Compare(JsonElement left, JsonElement right)
    {
        if (!IsFinite(left) || !IsFinite(right)) throw ControlPolicy.Denied();
        var a = Parts(left);
        var b = Parts(right);
        if (a.Sign != b.Sign) return a.Sign.CompareTo(b.Sign);
        if (a.Sign == 0) return 0;
        var order = a.Magnitude.CompareTo(b.Magnitude);
        if (order != 0) return order * a.Sign;
        for (var i = 0; i < Math.Max(a.Digits.Length, b.Digits.Length); i++)
        {
            var x = i < a.Digits.Length ? a.Digits[i] : '0';
            var y = i < b.Digits.Length ? b.Digits[i] : '0';
            if (x != y) return x.CompareTo(y) * a.Sign;
        }
        return 0;
    }

    private static (int Sign, BigInteger Magnitude, string Digits) Parts(JsonElement value)
    {
        var raw = value.GetRawText();
        var negative = raw[0] == '-';
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        var dot = mantissa.IndexOf('.');
        var fractionalDigits = dot < 0 ? 0 : mantissa.Length - dot - 1;
        var digits = mantissa.TrimStart('-').Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return (0, BigInteger.Zero, "");
        var exponent = exponentIndex < 0 ? BigInteger.Zero : BigInteger.Parse(raw.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return (negative ? -1 : 1, exponent + digits.Length - fractionalDigits, digits);
    }
}
