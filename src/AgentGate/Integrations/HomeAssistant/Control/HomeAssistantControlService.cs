using System.Text.Json;
using AgentGate.Policy;
using AgentGate.Tools;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AgentGate.Integrations.HomeAssistant.Control;

public sealed class HomeAssistantControlService(IServiceProvider services, ControlPolicy policy)
{
    // Egy folyamaton belül a hangerőállítás és felolvasás közé más gate-médiaművelet nem ékelődhet.
    private readonly SemaphoreSlim mediaExecution = new(1, 1);
    public ControlRequest Validate(IReadOnlyDictionary<string, JsonElement> arguments, RiskLevel risk)
    {
        var request = policy.Validate(arguments, risk);
        if (!Client.IsEntityAllowed(request.EntityId)) throw ControlPolicy.Denied();
        if (request.Domain == "media_player" && request.Service == "speak") policy.Media.CheckTime(risk, services.GetRequiredService<TimeProvider>());
        return request;
    }
    private HomeAssistantClient Client => services.GetService<HomeAssistantClient>() ?? throw new GateToolException("A Home Assistant nincs beállítva.");

    public async Task<ControlRequest> PrepareAsync(IReadOnlyDictionary<string, JsonElement> arguments, RiskLevel risk, CancellationToken cancellationToken)
    {
        var request = Validate(arguments, risk);
        await CheckFreshAsync(request, risk, cancellationToken);
        Validate(arguments, risk);
        if (await services.GetRequiredService<IKillSwitchStateSource>().IsEnabledAsync(cancellationToken)) throw ControlPolicy.Denied();
        return request;
    }

    private static void RequireUniqueFields(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !names.Add(p.Name))) throw ControlPolicy.Denied();
    }

    private async Task CheckFreshAsync(ControlRequest request, RiskLevel risk, CancellationToken cancellationToken)
    {
        if (request.Domain == "climate" && request.Service == "set_temperature")
        {
            var climate = await Client.GetStateAsync(request.EntityId, cancellationToken);
            var attributes = climate.Attributes;
            RequireUniqueFields(attributes);
            static JsonElement Bound(JsonElement attributes, string name)
            {
                if (!attributes.TryGetProperty(name, out var value) || !ControlNumber.IsFinite(value)) throw ControlPolicy.Denied();
                return value;
            }
            var min = Bound(attributes, "min_temp");
            var max = Bound(attributes, "max_temp");
            if (ControlNumber.Compare(min, max) > 0) throw ControlPolicy.Denied();
            var hasUnit = false;
            foreach (var name in new[] { "temperature_unit", "unit_of_measurement" })
            {
                if (!attributes.TryGetProperty(name, out var unit)) continue;
                hasUnit = true;
                if (unit.ValueKind != JsonValueKind.String || unit.GetString() != "°C") throw ControlPolicy.Denied();
            }
            if (!hasUnit)
            {
                var config = (await Client.GetConfigAsync(cancellationToken)).Data;
                RequireUniqueFields(config);
                if (!config.TryGetProperty("unit_system", out var units) || units.ValueKind != JsonValueKind.Object ||
                    !units.TryGetProperty("temperature", out var unit) || unit.ValueKind != JsonValueKind.String || unit.GetString() != "°C") throw ControlPolicy.Denied();
                RequireUniqueFields(units);
            }
            var requested = request.Data.GetProperty("temperature");
            // A statikus 10–30 korlátot a policy már pontosan ellenőrizte; ez a másik metszetfeltétel.
            if (ControlNumber.Compare(requested, min) < 0 || ControlNumber.Compare(requested, max) > 0) throw ControlPolicy.Denied();
            return;
        }
        if (request.Domain != "cover") return;
        var state = await Client.GetStateAsync(request.EntityId, cancellationToken);
        RequireUniqueFields(state.Attributes);
        if (!state.Attributes.TryGetProperty("device_class", out var value) || value.ValueKind != JsonValueKind.String) throw ControlPolicy.Denied();
        var deviceClass = value.GetString();
        var allowed = risk == RiskLevel.Control
            ? new[] { "awning", "blind", "curtain", "damper", "shade", "shutter", "window" }
            : new[] { "garage", "gate", "door" };
        if (!allowed.Contains(deviceClass, StringComparer.Ordinal)) throw ControlPolicy.Denied();
    }

    public async Task<JsonElement> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> arguments, RiskLevel risk, CancellationToken cancellationToken)
    {
        var request = Validate(arguments, risk);
        if (request.Domain != "media_player") return await ExecuteCoreAsync(arguments, risk, cancellationToken);
        await mediaExecution.WaitAsync(cancellationToken);
        try
        {
            return request.Service == "speak"
                ? await SpeakAsync(arguments, risk, cancellationToken)
                : await ExecuteCoreAsync(arguments, risk, cancellationToken);
        }
        finally { mediaExecution.Release(); }
    }

    private async Task<JsonElement> SpeakAsync(IReadOnlyDictionary<string, JsonElement> arguments, RiskLevel risk, CancellationToken cancellationToken)
    {
        var request = Validate(arguments, risk);
        var attempted = false;
        var speechPhase = false;
        var safetyRejected = false;
        TtsRateLimiter.Reservation? reservation = null;
        try
        {
            async Task Guard(CancellationToken ct)
            {
                Validate(arguments, risk);
                if (await services.GetRequiredService<IKillSwitchStateSource>().IsEnabledAsync(ct))
                {
                    safetyRejected = true;
                    throw ControlPolicy.Denied();
                }
                Validate(arguments, risk);
            }
            var volume = request.Data.GetProperty("volume_level");
            var volumeRequest = new ControlRequest("media_player", "volume_set", request.EntityId,
                JsonSerializer.SerializeToElement(new { volume_level = volume }));
            await Client.PostServiceAsync(volumeRequest, async ct =>
            {
                await Guard(ct);
                reservation = services.GetRequiredService<TtsRateLimiter>().TryReserve();
                if (reservation is null || !services.GetRequiredService<ControlRateLimiter>().TryAcquire(request.EntityId)) throw new ControlRateLimitException();
                attempted = true;
            }, cancellationToken);
            // A HA elfogadó válasza nem bizonyítja, hogy az eszköz tényleg átvette a hangerőt.
            await ConfirmVolumeAsync(request, volume, Guard, cancellationToken);
            var speech = new ControlRequest("tts", "speak", policy.Media.EngineEntityId!, JsonSerializer.SerializeToElement(new
            {
                media_player_entity_id = request.EntityId,
                message = request.Data.GetProperty("message").GetString(),
                cache = false
            }));
            speechPhase = true;
            await Client.PostServiceAsync(speech, async ct =>
            {
                await Guard(ct);
                reservation!.Complete();
            }, cancellationToken);
            return JsonSerializer.SerializeToElement(new { entity_id = request.EntityId, service = "speak", volume_level = volume, status = "elküldve" });
        }
        catch (TtsExecutionException) { throw; }
        catch (OperationCanceledException) when (attempted && cancellationToken.IsCancellationRequested)
        {
            throw new TtsExecutionException(TtsFailure.Cancelled);
        }
        catch (TtsQuietHoursException) when (attempted)
        {
            throw new TtsExecutionException(TtsFailure.QuietHours);
        }
        catch (HomeAssistantServiceRejectedException) when (attempted)
        {
            throw new TtsExecutionException(speechPhase ? TtsFailure.SpeechRejected : TtsFailure.VolumeRejected);
        }
        catch (GateToolException) when (attempted)
        {
            throw new TtsExecutionException(TtsFailure.SafetyDenied);
        }
        catch (Exception) when (attempted)
        {
            throw new TtsExecutionException(safetyRejected ? TtsFailure.SafetyDenied : TtsFailure.UncertainTransport);
        }
        finally { reservation?.Complete(); }
    }

    private async Task ConfirmVolumeAsync(ControlRequest request, JsonElement volume, Func<CancellationToken, Task> guard, CancellationToken cancellationToken)
    {
        var clock = services.GetRequiredService<TimeProvider>();
        var window = TimeSpan.FromSeconds(3);
        var started = clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(window, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var ct = linked.Token;
        try
        {
            // Legfeljebb hat GET: azonnal, majd az előző válasz után legalább 500 ms múlva.
            for (var attempt = 0; attempt < 6; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (clock.GetElapsedTime(started) >= window) break;
                await guard(ct).WaitAsync(ct);
                var state = await Client.GetStateAsync(request.EntityId, ct).WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                if (clock.GetElapsedTime(started) >= window) break;
                RequireUniqueFields(state.Attributes);
                if (state.State is not ("unknown" or "unavailable") && state.Attributes.TryGetProperty("volume_level", out var actual) &&
                    policy.Media.AllowsVolume(actual) && ControlNumber.Compare(actual, volume) <= 0) return;
                await Task.Delay(TimeSpan.FromMilliseconds(500), clock, ct);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TtsExecutionException(TtsFailure.VolumeConfirmationTimeout);
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new TtsExecutionException(TtsFailure.VolumeConfirmationTimeout);
    }

    private async Task<JsonElement> ExecuteCoreAsync(IReadOnlyDictionary<string, JsonElement> arguments, RiskLevel risk, CancellationToken cancellationToken)
    {
        var request = Validate(arguments, risk);
        var attempted = false;
        try
        {
            await Client.PostServiceAsync(request, async ct =>
            {
                await CheckFreshAsync(request, risk, ct);
                Validate(arguments, risk);
                if (await services.GetRequiredService<IKillSwitchStateSource>().IsEnabledAsync(ct)) throw ControlPolicy.Denied();
                if (!services.GetRequiredService<ControlRateLimiter>().TryAcquire(request.EntityId))
                    throw new ControlRateLimitException();
                attempted = true;
            }, cancellationToken);
            var state = await Client.GetStateAsync(request.EntityId, cancellationToken);
            if (!HomeAssistantReadProjection.IsSafe(state)) throw ControlPolicy.Denied();
            var result = HomeAssistantReadProjection.State(state);
            result["attributes"] = HomeAssistantReadProjection.Attributes(state.Attributes);
            return JsonSerializer.SerializeToElement(result);
        }
        catch (Exception) when (attempted)
        {
            throw new GateToolException("A vezérlési kísérlet vagy visszaolvasás sikertelen; a hatás megtörténhetett. Ez nem visszagörgetés; nincs újrapróbálás.");
        }
    }
}

public static class HomeAssistantControlRegistration
{
    private sealed class StartupOptions;
    public static IServiceCollection AddHomeAssistantControl(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(_ => ControlPolicy.FromConfiguration(configuration));
        services.TryAddSingleton<HomeAssistantControlService>();
        services.TryAddSingleton<ControlRateLimiter>();
        services.TryAddSingleton<TtsRateLimiter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<AgentGate.Approval.IApprovalOperationHandler, HomeAssistantControlOperation>());
        if (configuration.GetChildren().Any(c => c.Key.Equals("HomeAssistantControl", StringComparison.OrdinalIgnoreCase)))
            services.AddOptions<StartupOptions>().Configure<ControlPolicy, IServiceProvider>((_, policy, provider) =>
            {
                if (policy.Enabled && provider.GetService<HomeAssistantClient>() is null)
                    throw new OptionsValidationException(Options.DefaultName, typeof(ControlPolicy), [ControlPolicy.ConfigurationError]);
            }).ValidateOnStart();
        return services;
    }
}
