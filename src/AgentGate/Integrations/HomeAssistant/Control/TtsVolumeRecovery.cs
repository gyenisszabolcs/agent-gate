using System.Text.Json;

namespace AgentGate.Integrations.HomeAssistant.Control;

// Csak a hiányzó hangerő szerveroldali kezelése; nem hívható általános médiavezérlőként.
internal sealed class TtsVolumeRecovery(HomeAssistantClient client, TtsPolicy policy, TimeProvider clock)
{
    public async Task<JsonElement> ResolveAsync(string entity, Func<CancellationToken, Task> guard, Action attempting, CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromSeconds(3);
        var started = clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(window, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var ct = linked.Token;
        void CheckWindow()
        {
            ct.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) >= window) throw new TtsExecutionException(TtsFailure.VolumeConfirmationTimeout);
        }
        async Task Guard(CancellationToken token)
        {
            CheckWindow();
            await guard(token).WaitAsync(ct);
            CheckWindow();
        }
        async Task Post(string service, object data)
        {
            await Guard(ct);
            await client.PostServiceAsync(new ControlRequest("media_player", service, entity, JsonSerializer.SerializeToElement(data)), async token =>
            {
                await Guard(token);
                attempting();
            }, ct).WaitAsync(ct);
            CheckWindow();
        }
        var turnedOn = false;
        var unmutePending = false;
        JsonElement? target = null;
        long? lastRead = null;
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (lastRead is { } previous)
                {
                    var remaining = TimeSpan.FromMilliseconds(500) - clock.GetElapsedTime(previous);
                    while (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), clock, ct);
                        remaining = TimeSpan.FromMilliseconds(500) - clock.GetElapsedTime(previous);
                    }
                }
                await Guard(ct);
                var state = await client.GetStateAsync(entity, ct).WaitAsync(ct);
                CheckWindow();
                lastRead = clock.GetTimestamp();
                var attributes = state.Attributes;
                if (attributes.ValueKind != JsonValueKind.Object ||
                    attributes.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != attributes.EnumerateObject().Count())
                    throw new TtsExecutionException(TtsFailure.VolumeStateUnconfirmed);
                var readable = attributes.TryGetProperty("volume_level", out var volume) && ControlNumber.InRange(volume, 0, 1);
                var active = state.State is not ("off" or "unknown" or "unavailable");
                if (!active || !readable)
                {
                    if (!turnedOn && target is null)
                    {
                        turnedOn = true;
                        await Post("turn_on", new { });
                    }
                    continue;
                }
                if (!attributes.TryGetProperty("is_volume_muted", out var muted) || muted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new TtsExecutionException(TtsFailure.VolumeStateUnconfirmed);
                if (target is { } expected)
                {
                    if (policy.AllowsVolume(volume) && ControlNumber.Compare(volume, expected) == 0)
                    {
                        if (unmutePending && muted.ValueKind == JsonValueKind.True)
                        {
                            // Csak a még némított célhangerő HA-visszaigazolása után oldunk fel.
                            // A teljes végállapotot külön GET igazolja, ugyanebben az ablakban.
                            unmutePending = false;
                            await Post("volume_mute", new { is_volume_muted = false });
                            continue;
                        }
                        if (muted.ValueKind == JsonValueKind.False) return volume.Clone();
                    }
                    continue;
                }
                if (muted.ValueKind == JsonValueKind.True || ControlNumber.Compare(volume, JsonSerializer.SerializeToElement(0)) == 0)
                {
                    target = policy.FallbackVolume;
                    if (!policy.AllowsVolume(target.Value)) throw ControlPolicy.Denied();
                    unmutePending = muted.ValueKind == JsonValueKind.True;
                    // Az ACK nem állapotigazolás; a feloldás a következő olvasási fázis dolga.
                    await Post("volume_set", new { volume_level = target.Value });
                }
                else if (!policy.AllowsVolume(volume))
                {
                    target = policy.MaximumVolume;
                    await Post("volume_set", new { volume_level = target.Value });
                }
                else return volume.Clone();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TtsExecutionException(TtsFailure.VolumeConfirmationTimeout);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new TtsExecutionException(TtsFailure.VolumeConfirmationTimeout);
    }
}
