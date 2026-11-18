using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentGate.Policy;
using AgentGate.Secrets;
using AgentGate.Tools;
using Microsoft.Extensions.Options;

namespace AgentGate.Integrations.HomeAssistant.Control;

/// <summary>A hangkeltés változtathatatlan korlátai; az UTC óra helyi faliórává alakítása DST-biztos.</summary>
public sealed class TtsPolicy
{
    public JsonElement MaximumVolume { get; }
    public string? EngineEntityId { get; }
    private readonly TimeOnly start;
    private readonly TimeOnly end;
    private readonly TimeZoneInfo zone;

    internal TtsPolicy(IConfigurationSection root)
    {
        static OptionsValidationException Invalid() => new(Options.DefaultName, typeof(ControlPolicy), [ControlPolicy.ConfigurationError]);
        var maximum = root.GetSection("MediaPlayerMaxVolume");
        try
        {
            if (maximum.GetChildren().Any()) throw Invalid();
            if (root.GetChildren().Any(c => c.Key == "MediaPlayerMaxVolume") && maximum.Value is null) throw Invalid();
            using var json = JsonDocument.Parse(maximum.Value ?? "0.6");
            MaximumVolume = json.RootElement.Clone();
            if (!ControlNumber.InRange(MaximumVolume, 0, 1)) throw Invalid();
            var tts = root.GetSection("Tts");
            var keys = tts.GetChildren().ToArray();
            if (tts.Value is not null || root.GetChildren().Any(c => c.Key == "Tts") && keys.Length == 0 ||
                keys.Any(c => c.Key is not ("EngineEntityId" or "QuietHoursStart" or "QuietHoursEnd" or "TimeZone") || c.Value is null || c.GetChildren().Any())) throw Invalid();
            EngineEntityId = tts["EngineEntityId"];
            if (EngineEntityId is not null && (!ControlPolicy.CanonicalEntity(EngineEntityId) || !EngineEntityId.StartsWith("tts.", StringComparison.Ordinal))) throw Invalid();
            if (!TimeOnly.TryParseExact(tts["QuietHoursStart"] ?? "22:00", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out start) ||
                !TimeOnly.TryParseExact(tts["QuietHoursEnd"] ?? "07:00", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out end) || start == end) throw Invalid();
            zone = TimeZoneInfo.FindSystemTimeZoneById(tts["TimeZone"] ?? "Europe/Budapest");
        }
        catch (Exception) { throw Invalid(); }
    }

    public bool IsQuiet(DateTimeOffset utc)
    {
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);
        return start < end ? local >= start && local < end : local >= start || local < end;
    }

    public void CheckTime(RiskLevel risk, TimeProvider clock)
    {
        if (risk == RiskLevel.Control && IsQuiet(clock.GetUtcNow()))
            throw new TtsQuietHoursException();
    }

    public bool AllowsVolume(JsonElement value) => ControlNumber.InRange(value, 0, 1) && ControlNumber.Compare(value, MaximumVolume) <= 0;

    public static string PlainText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw ControlPolicy.Denied();
        var text = value.GetString()!;
        // Szándékosan szűk ábécé: szöveg és központozás, nem URL, útvonal, SSML, sablon vagy escape.
        if (string.IsNullOrWhiteSpace(text) || text.Length > 200 ||
            !Regex.IsMatch(text, @"\A[\p{L}\p{M}\p{N} .,!?;:'""()=+%\-\[\]]+\z", RegexOptions.CultureInvariant)) throw ControlPolicy.Denied();
        var clean = SecretRedactor.RedactText(text)!;
        if (clean.Length > 200) throw ControlPolicy.Denied();
        return clean;
    }
}

internal sealed class TtsQuietHoursException() : Exception(SafeMessage)
{
    internal const string SafeMessage = "Csendes órákban a felolvasás csak a ha_sensitive_control jóváhagyási útján kérhető.";
}

internal enum TtsFailure
{
    VolumeConfirmationTimeout, VolumeRejected, SpeechRejected, UncertainTransport, SafetyDenied, QuietHours, Cancelled
}

// Csak a végrehajtó állítja elő. Nincs tetszőleges szöveg, providerüzenet vagy belső kivétel.
internal sealed class TtsExecutionException(TtsFailure failure) : Exception(MessageFor(failure))
{
    internal string SafeMessage => MessageFor(failure);
    private static string MessageFor(TtsFailure value) => (value switch
    {
        TtsFailure.VolumeConfirmationTimeout => "A hangerő-visszaigazolás nem érkezett meg 3 másodpercen belül.",
        TtsFailure.VolumeRejected => "A Home Assistant elutasította a hangerőállítást.",
        TtsFailure.SpeechRejected => "A Home Assistant elutasította a felolvasást.",
        TtsFailure.UncertainTransport => "A Home Assistant hívás eredménye bizonytalan (transport- vagy válaszhiba).",
        TtsFailure.QuietHours => TtsQuietHoursException.SafeMessage,
        TtsFailure.Cancelled => "A felolvasási művelet megszakadt.",
        _ => "A felolvasást a végrehajtási biztonsági ellenőrzés letiltotta."
    }) + " A hangerőállítás vagy a felolvasás hatása már megtörténhetett; nincs visszagörgetés vagy újrapróbálás.";
}

/// <summary>Mindkét út és minden cél között közös, atomikus, monoton gördülő TTS-kvóta.</summary>
public sealed class TtsRateLimiter(TimeProvider clock)
{
    private readonly TimeProvider time = clock;
    private readonly object sync = new();
    private readonly List<Reservation> attempts = [];
    public Reservation? TryReserve()
    {
        lock (sync)
        {
            var now = time.GetTimestamp();
            attempts.RemoveAll(item => item.Completed && time.GetElapsedTime(item.Timestamp, now) >= TimeSpan.FromMinutes(1));
            if (attempts.Count >= 3) return null;
            var reservation = new Reservation(this);
            attempts.Add(reservation);
            return reservation;
        }
    }

    public sealed class Reservation
    {
        private readonly TtsRateLimiter owner;
        internal bool Completed { get; private set; }
        internal long Timestamp { get; private set; }
        internal Reservation(TtsRateLimiter owner) => this.owner = owner;

        // A hely az előkészítés alatt is foglalt. A 60 másodperc csak a beszéd POST-jától
        // (vagy a meghiúsult kísérlet végétől) indul, nem a korábbi hangerőállítástól.
        public void Complete()
        {
            lock (owner.sync)
            {
                if (Completed) return;
                Timestamp = owner.time.GetTimestamp();
                Completed = true;
            }
        }
    }
}
