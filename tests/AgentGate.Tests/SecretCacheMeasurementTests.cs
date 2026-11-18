using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Policy;
using AgentGate.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using static AgentGate.Tests.CachedSecretProviderTests;
using static AgentGate.Tests.HomeAssistantSecretCacheTests;

namespace AgentGate.Tests;

// Kizárólag mockolt I/O; Stopwatch faliórás idők, virtuális óra csak a TTL előreléptetéséhez.
public sealed class SecretCacheMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SlowProviderBeforeAfterWithColdWarmExpiredAndUnauthorizedPaths()
    {
        const int repetitions = 3;
        var clock = new Clock();
        var raw = new Source { Read = async (_, ct) => { await Task.Delay(150, ct); return NewValue(); } };
        await using var cache = new CachedSecretProvider(raw, clock);
        var id = Guid.NewGuid();
        var unauthorized = false;
        var handler = new Handler(async (_, request) =>
        {
            await Task.Delay(5);
            if (unauthorized) { unauthorized = false; return Json("", HttpStatusCode.Unauthorized); }
            return Json(request.RequestUri!.AbsolutePath.StartsWith("/api/states/", StringComparison.Ordinal)
                ? "{\"entity_id\":\"sensor.room\",\"state\":\"on\",\"attributes\":{}}" : "[]");
        });
        var beforeHandler = new Handler(async (_, request) =>
        {
            await Task.Delay(5);
            return Json(request.RequestUri!.AbsolutePath.StartsWith("/api/states/", StringComparison.Ordinal)
                ? "{\"entity_id\":\"sensor.room\",\"state\":\"on\",\"attributes\":{}}" : "[]");
        });
        using var before = Client(raw, id, beforeHandler);
        using var after = Client(cache, id, handler);
        output.WriteLine("Mock: provider=Task.Delay(150 ms), HTTP=Task.Delay(5 ms), n=3/fázis/művelet; idő=Stopwatch; TTL=virtuális 5 perc; nincs éles I/O.");
        foreach (var operation in new[] { "ha_get_state", "ha_control", "ha_list_areas" })
        {
            async Task Invoke(AgentGate.Integrations.HomeAssistant.HomeAssistantClient client)
            {
                if (operation == "ha_get_state") await client.GetStateAsync("sensor.room");
                else if (operation == "ha_list_areas") await client.GetAreasAsync();
                else await client.PostServiceAsync(new ControlRequest("light", "turn_on", "light.room", JsonSerializer.SerializeToElement(new { })),
                    _ => Task.CompletedTask, CancellationToken.None);
            }
            async Task Measure(string phase, Func<Task> action, int expectedFetches)
            {
                var startCalls = raw.Calls;
                var startHttp = handler.Calls + beforeHandler.Calls;
                var times = new List<double>();
                for (var i = 0; i < repetitions; i++)
                {
                    var watch = Stopwatch.StartNew();
                    await action();
                    times.Add(watch.Elapsed.TotalMilliseconds);
                }
                Assert.Equal(expectedFetches, raw.Calls - startCalls);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{operation} {phase}: n={repetitions}, ms=[{string.Join(", ", times.Select(t => t.ToString("F2", CultureInfo.InvariantCulture)))}], provider={raw.Calls - startCalls}, HTTP={handler.Calls + beforeHandler.Calls - startHttp}"));
            }
            await Measure("előtte/cache nélkül", () => Invoke(before), 3);
            await Measure("hideg/kényszerített miss", async () => { cache.Invalidate(id); await Invoke(after); }, 3);
            await Measure("meleg", () => Invoke(after), 0);
            await Measure("lejárt", async () => { clock.Advance(TimeSpan.FromMinutes(5)); await Invoke(after); }, 3);
            await Measure("401 és következő külön hívás", async () =>
            {
                unauthorized = true;
                var error = await Record.ExceptionAsync(() => Invoke(after));
                Assert.True(error is AgentGate.Integrations.HomeAssistant.HomeAssistantException);
                await Invoke(after);
            }, 3);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualTtsConfirmationWithSlowProviderRespectsUnchangedWindowAndReadSpacing(bool cached)
    {
        // A TTS tényleges service-kódja fut: előbb volume_set, majd legfeljebb 3 s ellenőrzés.
        var raw = new Source { Read = async (_, ct) => { await Task.Delay(1100, ct); return NewValue(); } };
        await using var cache = new CachedSecretProvider(raw, TimeProvider.System);
        var id = Guid.NewGuid();
        var reads = new List<long>();
        long volumeResponse = 0;
        long confirmedAt = 0;
        var posts = 0;
        var handler = new Handler((_, request) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                if (posts == 1) volumeResponse = Stopwatch.GetTimestamp();
                return Task.FromResult(Json("[]"));
            }
            reads.Add(Stopwatch.GetTimestamp());
            var response = Json("{\"entity_id\":\"media_player.room\",\"state\":\"on\",\"attributes\":{\"volume_level\":" +
                (reads.Count >= 3 ? "0.4" : "0.9") + "}}");
            if (reads.Count >= 3) confirmedAt = Stopwatch.GetTimestamp();
            return Task.FromResult(response);
        });
        using var ha = Client(cached ? cache : raw, id, handler);
        await using var fixture = new ControlFixture(services =>
        {
            services.AddSingleton(HomeAssistantMediaPlayerTests.Policy());
            services.AddSingleton<TimeProvider>(new NoonClock());
            services.AddSingleton(ha);
        }, ["media_player"]);
        // Hostindítás és cache-feltöltés kívül a mért végrehajtáson; a feltöltést külön jelentjük.
        var service = fixture.App.Services.GetRequiredService<HomeAssistantControlService>();
        var fill = Stopwatch.StartNew();
        if (cached) await cache.GetSecretAsync(id);
        var fillMs = fill.Elapsed.TotalMilliseconds;
        var startingCalls = raw.Calls;
        var watch = Stopwatch.StartNew();
        var error = await Record.ExceptionAsync(() => service.ExecuteAsync(
            HomeAssistantControlPolicyTests.Arguments("media_player", "speak", "{\"message\":\"Teszt\",\"volume_level\":0.4}"),
            RiskLevel.Control, CancellationToken.None));
        var totalMs = watch.Elapsed.TotalMilliseconds;
        var completedAfterVolumeMs = Stopwatch.GetElapsedTime(volumeResponse).TotalMilliseconds;
        double? confirmationMs = confirmedAt == 0 ? null : Stopwatch.GetElapsedTime(volumeResponse, confirmedAt).TotalMilliseconds;
        // A faliórás proof hibája esetén is legyen megfigyelhető a tényleges GET-köz.
        output.WriteLine("Mért GET-közök (Stopwatch, ms): [" + string.Join(", ", reads.Zip(reads.Skip(1),
            (left, right) => Stopwatch.GetElapsedTime(left, right).TotalMilliseconds.ToString("F4", CultureInfo.InvariantCulture))) + "]");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TTS {(cached ? "meleg cache" : "előtte/cache nélkül")}: n=1, provider=Task.Delay(1100 ms), HTTP=azonnali mock, idő=Stopwatch, feltöltés={fillMs:F2} ms, végrehajtás={totalMs:F2} ms, sikeres confirmation GET={(confirmationMs?.ToString("F2", CultureInfo.InvariantCulture) ?? "nincs")} ms, volume-válasz utáni teljes idő={completedAfterVolumeMs:F2} ms, provider(végrehajtás)={raw.Calls - startingCalls}, GET={reads.Count}, POST={posts}, GET-idők=[{string.Join(", ", reads.Select(t => Stopwatch.GetElapsedTime(volumeResponse, t).TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture)))}] ms"));
        for (var i = 1; i < reads.Count; i++)
            Assert.True(Stopwatch.GetElapsedTime(reads[i - 1], reads[i]) >= TimeSpan.FromMilliseconds(500), "Túl sűrű ellenőrző GET.");
        if (cached)
        {
            Assert.True(error is null, "A meleg cache TTS-ellenőrzése nem sikerült.");
            Assert.Equal(3, reads.Count);
            Assert.Equal(2, posts);
            Assert.Equal(0, raw.Calls - startingCalls);
            Assert.True(confirmationMs is < 3000, "A sikeres confirmation GET kicsúszott az ablakból.");
        }
        else
        {
            Assert.True(error is not null && error.Message.StartsWith("A hangerő-visszaigazolás nem érkezett meg 3 másodpercen belül.", StringComparison.Ordinal),
                "A lassú kontroll nem az ellenőrzési ablakon állt meg.");
            Assert.Equal(1, posts);
            // A szálütemezés csökkentheti az ablakba férő olvasások számát; ez nem teljesítménygarancia.
            Assert.InRange(reads.Count, 1, 2);
        }
    }

    private sealed class NoonClock : TimeProvider
    {
        // Csak a napszakot rögzítjük; timestamp és időzítő valódi, monoton rendszeridő.
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-18T12:00:00Z", CultureInfo.InvariantCulture);
    }
}
