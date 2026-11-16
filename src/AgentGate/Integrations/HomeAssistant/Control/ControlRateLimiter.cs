namespace AgentGate.Integrations.HomeAssistant.Control;

/// <summary>Közös, atomikus, monoton gördülő írási kvóta; elutasítás nem foglal részkeretet.</summary>
public sealed class ControlRateLimiter(TimeProvider clock, ControlPolicy policy)
{
    private readonly object sync = new();
    private readonly Queue<(long Timestamp, string Entity)> attempts = new();
    private readonly Dictionary<string, int> counts = new(StringComparer.Ordinal);
    // Konfigurációtól független biztonságos memóriaplafon: telítve zártan hibázik.
    private const int MaximumTrackedAttempts = 65536;
    public bool TryAcquire(string entity)
    {
        lock (sync)
        {
            var now = clock.GetTimestamp();
            while (attempts.TryPeek(out var oldest) && clock.GetElapsedTime(oldest.Timestamp, now) >= TimeSpan.FromMinutes(1))
            {
                attempts.Dequeue();
                if (--counts[oldest.Entity] == 0) counts.Remove(oldest.Entity);
            }
            var count = counts.GetValueOrDefault(entity);
            if (count >= policy.PerEntityPerMinute || attempts.Count >= policy.GlobalPerMinute || attempts.Count >= MaximumTrackedAttempts) return false;
            attempts.Enqueue((now, entity));
            counts[entity] = count + 1;
            return true;
        }
    }
}
