using System.Net;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Secrets;
using Microsoft.Extensions.Options;
using Xunit;
using static AgentGate.Tests.CachedSecretProviderTests;

namespace AgentGate.Tests;

public sealed class HomeAssistantSecretCacheTests
{
    [Fact]
    public async Task VolumePostUnauthorizedKeepsSafeRejectionAndNeverRepeatsWrite()
    {
        var raw = new Source();
        await using var cache = new CachedSecretProvider(raw, new Clock());
        var id = Guid.NewGuid();
        var old = await cache.GetSecretLeaseAsync(id);
        var handler = new Handler((_, request) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            return Task.FromResult(Json(old.Value, HttpStatusCode.Unauthorized));
        });
        using var client = Client(cache, id, handler);
        var request = new AgentGate.Integrations.HomeAssistant.Control.ControlRequest("media_player", "volume_set", "media_player.room",
            System.Text.Json.JsonSerializer.SerializeToElement(new { volume_level = 0.4 }));
        var error = await Record.ExceptionAsync(() => client.PostServiceAsync(request, _ => Task.CompletedTask, CancellationToken.None));
        Assert.True(error is HomeAssistantServiceRejectedException && error.InnerException is null &&
            !error.ToString().Contains(old.Value, StringComparison.Ordinal), "A volume POST nem biztonságos elutasítás.");
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, raw.Calls);
        Assert.NotSame(old, await cache.GetSecretLeaseAsync(id));
        Assert.Equal(2, raw.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnauthorizedInvalidatesUsedSecretWithoutGetOrPostRetry(bool post)
    {
        var raw = new Source();
        await using var cache = new CachedSecretProvider(raw, new Clock());
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var other = await cache.GetSecretLeaseAsync(otherId);
        var old = await cache.GetSecretLeaseAsync(id);
        var handler = new Handler((call, request) =>
        {
            Assert.Equal(post ? HttpMethod.Post : HttpMethod.Get, request.Method);
            if (call == 1)
            {
                Assert.True(request.Headers.Authorization?.Parameter == old.Value, "Nem a várt generáció ment ki.");
                return Task.FromResult(Json(old.Value, HttpStatusCode.Unauthorized));
            }
            Assert.True(request.Headers.Authorization?.Parameter != old.Value, "Visszavont token ment ki.");
            return Task.FromResult(Json(post ? "[]" : "{}"));
        });
        using var client = Client(cache, id, handler);
        async Task Invoke() { if (post) await client.GetAreasAsync(); else await client.GetConfigAsync(); }
        var error = await Record.ExceptionAsync(Invoke);
        Assert.True(error is HomeAssistantException && error.InnerException is null &&
            !error.ToString().Contains(old.Value, StringComparison.Ordinal), "A 401 titkot szivárogtatott.");
        Assert.Equal(1, handler.Calls);
        Assert.Equal(2, raw.Calls);
        await Invoke();
        Assert.Equal(2, handler.Calls);
        Assert.Equal(3, raw.Calls);
        Assert.Same(other, await cache.GetSecretLeaseAsync(otherId));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task OtherHttpErrorsDoNotEvictOrRetry(int status)
    {
        var raw = new Source();
        await using var cache = new CachedSecretProvider(raw, new Clock());
        var id = Guid.NewGuid();
        var warm = await cache.GetSecretLeaseAsync(id);
        var handler = new Handler((_, _) => Task.FromResult(Json("", (HttpStatusCode)status)));
        using var client = Client(cache, id, handler);
        await Assert.ThrowsAsync<HomeAssistantException>(() => client.GetConfigAsync());
        Assert.Same(warm, await cache.GetSecretLeaseAsync(id));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, raw.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateOldUnauthorizedDoesNotEvictNewGenerationOrInflightRefresh(bool stillFetching)
    {
        var value = NewValue();
        var entered = Signal();
        var releaseFetch = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = new Source { Read = (call, _) => call == 1 ? Task.FromResult(value) : releaseFetch.Task };
        await using var cache = new CachedSecretProvider(raw, new Clock());
        var id = Guid.NewGuid();
        var old = await cache.GetSecretLeaseAsync(id);
        var late401 = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _) => { entered.SetResult(); return late401.Task; });
        using var client = Client(cache, id, handler);
        var request = client.GetConfigAsync();
        await entered.Task;
        cache.Invalidate(id, old);
        var next = cache.GetSecretLeaseAsync(id);
        if (!stillFetching) { releaseFetch.SetResult(value); await next; }
        late401.SetResult(Json(value, HttpStatusCode.Unauthorized));
        var error = await Record.ExceptionAsync(() => request);
        Assert.True(error is HomeAssistantException && !error.ToString().Contains(value, StringComparison.Ordinal), "Nem biztonságos késői 401.");
        if (stillFetching) releaseFetch.SetResult(value);
        var fresh = await next;
        Assert.NotSame(old, fresh);
        Assert.Same(fresh, await cache.GetSecretLeaseAsync(id));
        Assert.Equal(2, raw.Calls);
        Assert.Equal(1, handler.Calls);
    }

    internal static HomeAssistantClient Client(ISecretProvider provider, Guid id, HttpMessageHandler handler) => new(
        Options.Create(new HomeAssistantOptions
        {
            BaseUrl = "https://ha.example/", TokenSecretId = id.ToString(),
            DomainAllowlist = ["sensor", "light", "media_player"], Timeout = TimeSpan.FromSeconds(10)
        }), provider, handler);

    internal static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    internal sealed class Handler(Func<int, HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(Interlocked.Increment(ref calls), request);
    }
}
