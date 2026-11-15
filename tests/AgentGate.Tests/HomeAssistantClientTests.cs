using System.Net;
using System.Text;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Secrets;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantClientTests
{
    [Theory]
    [InlineData("https://ha.example/", false)]
    [InlineData("https://ha.example/", true)]
    [InlineData("http://10.1.2.3:8123/", true)]
    [InlineData("http://172.16.1.2/", true)]
    [InlineData("http://192.168.1.2/", true)]
    public async Task ConfigUsesExactGetAndRuntimeBearer(string baseUrl, bool allowHttp)
    {
        var secrets = new FakeSecrets();
        var options = ValidOptions();
        options.BaseUrl = baseUrl;
        options.AllowHttp = allowHttp;
        var handler = new FakeHandler((request, ct) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(baseUrl + "api/config", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.True(request.Headers.Authorization?.Parameter == secrets.Token, "A Bearer továbbítás hibás.");
            Assert.True(ct.CanBeCanceled);
            return Task.FromResult(Json("{\"version\":\"2026.11\"}"));
        });
        using var client = new HomeAssistantClient(Options.Create(options), secrets, handler);
        var result = await client.GetConfigAsync(CancellationToken.None);
        Assert.Equal("2026.11", result.Data.GetProperty("version").GetString());
        Assert.Equal(Guid.Parse(options.TokenSecretId!), secrets.RequestedId);
        Assert.True(secrets.CancellationToken.CanBeCanceled);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("http://192.168.1.2/", false)]
    [InlineData("http://ha.example/", true)]
    [InlineData("http://8.8.8.8/", true)]
    [InlineData("http://[::ffff:10.0.0.1]/", true)]
    public void ConstructorRejectsUnsafeHttpBeforeAnyIo(string baseUrl, bool allowHttp)
    {
        var options = ValidOptions();
        options.BaseUrl = baseUrl;
        options.AllowHttp = allowHttp;
        var secrets = new FakeSecrets();
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Nem történhet hálózati művelet."));
        var error = Assert.Throws<OptionsValidationException>(() =>
        {
            using var client = new HomeAssistantClient(Options.Create(options), secrets, handler);
        });
        Assert.Equal(HomeAssistantOptionsValidator.SafeMessage, Assert.Single(error.Failures));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(Guid.Empty, secrets.RequestedId);
    }

    [Fact]
    public async Task StatesListFiltersEveryDisallowedOrMalformedEntity()
    {
        var secrets = new FakeSecrets();
        var handler = new FakeHandler((request, ct) =>
        {
            Assert.True(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == secrets.Token, "A lista Bearer továbbítása hibás.");
            Assert.True(ct.CanBeCanceled);
            Assert.Equal("/api/states", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json("""
                [{"entity_id":"sensor.room","state":"21","attributes":{}},
                 {"entity_id":"lock.front","state":"locked","attributes":{}},
                 {"entity_id":"switch.desk","state":"on","attributes":{}},
                 {"entity_id":"sensor.bad/path","state":"bad","attributes":{}}]
                """));
        });
        using var client = new HomeAssistantClient(Options.Create(ValidOptions()), secrets, handler);
        var states = await client.GetStatesAsync();
        Assert.Equal("sensor.room", Assert.Single(states).EntityId);
    }

    [Theory]
    [InlineData("sensor.room", true)]
    [InlineData("light.desk_2", true)]
    [InlineData("lock.front", false)]
    [InlineData("sensor.x/../config", false)]
    [InlineData("sensor.x?token=x", false)]
    [InlineData("sensor.x#fragment", false)]
    [InlineData("sensor.%2f", false)]
    [InlineData("sensor.árvíz", false)]
    [InlineData("Sensor.room", false)]
    [InlineData("sensor.x\n", false)]
    [InlineData("sensor.", false)]
    [InlineData(".room", false)]
    [InlineData("sensor.a.b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task IndividualStateValidatesBeforeAnyIo(string? entityId, bool allowed)
    {
        var secrets = new FakeSecrets();
        var handler = new FakeHandler((request, ct) =>
        {
            Assert.True(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == secrets.Token, "Az entitás Bearer továbbítása hibás.");
            Assert.True(ct.CanBeCanceled);
            Assert.Equal("/api/states/" + entityId, request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(JsonSerializer.Serialize(new { entity_id = entityId, state = "on", attributes = new { friendly_name = "Nappali" } })));
        });
        using var client = new HomeAssistantClient(Options.Create(ValidOptions()), secrets, handler);
        if (allowed)
        {
            var state = await client.GetStateAsync(entityId!);
            Assert.Equal(entityId, state.EntityId);
            Assert.Equal("on", state.State);
            Assert.Equal("Nappali", state.Attributes.GetProperty("friendly_name").GetString());
        }
        else
        {
            await Assert.ThrowsAsync<HomeAssistantException>(() => client.GetStateAsync(entityId!));
            Assert.Equal(0, handler.Calls);
            Assert.Equal(Guid.Empty, secrets.RequestedId);
        }
    }

    [Fact]
    public async Task MissingAllowlistDeniesIndividualAndFiltersWholeList()
    {
        var options = ValidOptions();
        options.DomainAllowlist = null;
        var handler = new FakeHandler((_, _) => Task.FromResult(Json("[{\"entity_id\":\"sensor.room\",\"state\":\"on\",\"attributes\":{}}]")));
        using var client = new HomeAssistantClient(Options.Create(options), new FakeSecrets(), handler);
        await Assert.ThrowsAsync<HomeAssistantException>(() => client.GetStateAsync("sensor.room"));
        Assert.Equal(0, handler.Calls);
        Assert.Empty(await client.GetStatesAsync());
    }

    [Theory]
    [InlineData("config")]
    [InlineData("states")]
    [InlineData("state")]
    public async Task SuccessfulDtosRecursivelyRemoveSensitiveKeysAndUrlCredentials(string endpoint)
    {
        var secrets = new FakeSecrets();
        var hidden = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var payload = JsonSerializer.Serialize(new
        {
            friendly_name = "Nappali",
            nested = new object[] { new { Password = hidden, api_key = hidden, authorization = hidden, credential = hidden, safe = "érték" } },
            entity_picture = "/api/camera_proxy/camera.front?access_token=" + hidden,
            url = "https://user:" + hidden + "@ha.example/image?access_token=" + hidden + "#" + hidden,
            relative = "/image?foo=bar&access%5ftoken=" + hidden,
            note = secrets.Token,
            refreshToken = hidden
        });
        var body = endpoint == "config" ? payload :
            "{\"entity_id\":\"camera.front\",\"state\":\"on\",\"attributes\":" + payload + "}";
        if (endpoint == "states") body = "[" + body + "]";
        var handler = new FakeHandler((_, _) => Task.FromResult(Json(body)));
        using var client = new HomeAssistantClient(Options.Create(ValidOptions()), secrets, handler);
        object result = endpoint switch
        {
            "config" => await client.GetConfigAsync(),
            "states" => await client.GetStatesAsync(),
            _ => await client.GetStateAsync("camera.front")
        };
        var serialized = JsonSerializer.Serialize(result);
        Assert.True(!serialized.Contains(hidden, StringComparison.Ordinal), "Érzékeny attribútum maradt a DTO-ban.");
        Assert.True(!serialized.Contains(secrets.Token, StringComparison.Ordinal), "A hitelesítési token megjelent a DTO-ban.");
        Assert.Contains("Nappali", serialized);
        Assert.DoesNotContain("access_token", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("redirect")]
    [InlineData("json")]
    [InlineData("transport")]
    [InlineData("secret")]
    [InlineData("empty-token")]
    [InlineData("header-token")]
    public async Task FailuresNeverExposeBodiesHeadersTokensOrInnerExceptions(string failure)
    {
        var secrets = new FakeSecrets();
        if (failure == "secret") secrets.Read = _ => throw new InvalidOperationException(secrets.Token);
        if (failure == "empty-token") secrets.Read = _ => Task.FromResult("");
        if (failure == "header-token") secrets.Read = _ => Task.FromResult(secrets.Token + "\r\nInjected: header");
        var handler = new FakeHandler((_, _) =>
        {
            if (failure == "transport") throw new HttpRequestException(secrets.Token);
            var response = Json(failure == "json" ? "{\"" + secrets.Token : secrets.Token,
                failure == "http" ? HttpStatusCode.Unauthorized : failure == "redirect" ? HttpStatusCode.Found : HttpStatusCode.OK);
            response.ReasonPhrase = secrets.Token;
            response.Headers.TryAddWithoutValidation("X-Secret", secrets.Token);
            response.Headers.Location = new Uri("https://other.example/?access_token=" + secrets.Token);
            return Task.FromResult(response);
        });
        using var client = new HomeAssistantClient(Options.Create(ValidOptions()), secrets, handler);
        var caught = await Record.ExceptionAsync(() => client.GetConfigAsync());
        Assert.True(caught is HomeAssistantException, "A lekérés nem biztonságos típusú hibát adott.");
        var error = (HomeAssistantException)caught;
        Assert.True(error.Message == "A Home Assistant lekérés sikertelen.", "A hibaüzenet nem a rögzített biztonságos szöveg.");
        Assert.True(error.InnerException is null, "Belső kivétel szivárgott.");
        Assert.True(!error.ToString().Contains(secrets.Token, StringComparison.Ordinal), "A hiba titkot tartalmaz.");
        Assert.True(handler.Calls <= 1, "Átirányítás vagy ismétlés történt.");
        if (failure is "secret" or "empty-token" or "header-token") Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("config", "[]")]
    [InlineData("states", "{}")]
    [InlineData("states", "[null]")]
    [InlineData("states", "[{}]")]
    [InlineData("state", "{}")]
    [InlineData("state", "{\"entity_id\":\"lock.front\",\"state\":\"on\",\"attributes\":{}}")]
    [InlineData("state", "{\"entity_id\":\"sensor.other\",\"state\":\"on\",\"attributes\":{}}")]
    [InlineData("state", "{\"entity_id\":\"sensor.room\",\"state\":null,\"attributes\":{}}")]
    [InlineData("state", "{\"entity_id\":\"sensor.room\",\"state\":\"on\",\"attributes\":[]}")]
    public async Task InvalidJsonShapeFailsSafely(string endpoint, string body)
    {
        using var client = new HomeAssistantClient(Options.Create(ValidOptions()), new FakeSecrets(),
            new FakeHandler((_, _) => Task.FromResult(Json(body))));
        var error = await Assert.ThrowsAsync<HomeAssistantException>(async () =>
        {
            if (endpoint == "config") await client.GetConfigAsync();
            else if (endpoint == "states") await client.GetStatesAsync();
            else await client.GetStateAsync("sensor.room");
        });
        Assert.Equal("A Home Assistant válasz érvénytelen.", error.Message);
        Assert.True(error.InnerException is null, "Belső kivétel szivárgott.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TimeoutAndCallerCancellationAreSafeAndReachSecretOrHttp(bool cancelByCaller, bool duringSecret)
    {
        var secrets = new FakeSecrets();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Block(CancellationToken ct)
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { throw new OperationCanceledException(secrets.Token); }
            return secrets.Token;
        }
        if (duringSecret) secrets.Read = Block;
        var handler = new FakeHandler(async (_, ct) => { await Block(ct); return Json("{}"); });
        var options = ValidOptions();
        options.Timeout = cancelByCaller ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(60);
        using var client = new HomeAssistantClient(Options.Create(options), secrets, handler);
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetConfigAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (cancelByCaller) cancellation.Cancel();
        var caught = await Record.ExceptionAsync(() => pending);
        if (cancelByCaller)
        {
            Assert.True(caught is OperationCanceledException, "A megszakítás hibás kivételt adott.");
            var error = (OperationCanceledException)caught;
            Assert.True(error.Message == "A Home Assistant lekérés megszakítva.", "Nem biztonságos megszakítási üzenet.");
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(error.InnerException is null, "Belső kivétel szivárgott.");
        }
        else
        {
            Assert.True(caught is HomeAssistantException, "Az időkorlát hibás kivételt adott.");
            var error = (HomeAssistantException)caught;
            Assert.True(error.Message == "A Home Assistant lekérés időkorlátja lejárt.", "Nem biztonságos időkorlátüzenet.");
            Assert.True(error.InnerException is null, "Belső kivétel szivárgott.");
        }
        Assert.Equal(duringSecret ? 0 : 1, handler.Calls);
    }

    [Fact]
    public async Task ParallelHttpTimeoutsUseTheOperationDeadline()
    {
        var prematureHttpCancellations = 0;
        var incorrectFailures = 0;
        for (var batch = 0; batch < 8; batch++)
        {
            await Task.WhenAll(Enumerable.Range(0, 128).Select(async _ =>
            {
                var secrets = new FakeSecrets();
                var handler = new FakeHandler(async (_, ct) =>
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    catch (OperationCanceledException)
                    {
                        if (!secrets.CancellationToken.IsCancellationRequested)
                            Interlocked.Increment(ref prematureHttpCancellations);
                        throw new OperationCanceledException(secrets.Token);
                    }
                    return Json("{}");
                });
                var options = ValidOptions();
                options.Timeout = TimeSpan.FromMilliseconds(60);
                using var client = new HomeAssistantClient(Options.Create(options), secrets, handler);
                var caught = await Record.ExceptionAsync(() => client.GetConfigAsync());
                if (caught is not HomeAssistantException ||
                    caught.Message != "A Home Assistant lekérés időkorlátja lejárt." || caught.InnerException is not null ||
                    caught.ToString().Contains(secrets.Token, StringComparison.Ordinal))
                    Interlocked.Increment(ref incorrectFailures);
            }));
        }
        Assert.True(prematureHttpCancellations == 0,
            $"A HTTP megszakítása megelőzte a műveleti határidőt: {prematureHttpCancellations}; hibás besorolás: {incorrectFailures}.");
        Assert.True(incorrectFailures == 0, $"Hibás vagy nem biztonságos időkorlát-besorolás: {incorrectFailures}.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentProviderCancellationIsNotReportedAsTimeout(bool duringSecret)
    {
        var secrets = new FakeSecrets();
        if (duringSecret) secrets.Read = _ => throw new OperationCanceledException(secrets.Token);
        var handler = new FakeHandler((_, _) => throw new OperationCanceledException(secrets.Token));
        using var client = new HomeAssistantClient(Options.Create(ValidOptions()), secrets, handler);
        var caught = await Record.ExceptionAsync(() => client.GetConfigAsync());
        Assert.True(caught is HomeAssistantException, "A szolgáltatói hiba típusa hibás.");
        Assert.True(caught.Message == "A Home Assistant lekérés sikertelen.", "A szolgáltatói hibát időkorlátnak minősítette.");
        Assert.True(caught.InnerException is null && !caught.ToString().Contains(secrets.Token, StringComparison.Ordinal),
            "A szolgáltatói hiba titkot tartalmaz.");
        Assert.Equal(duringSecret ? 0 : 1, handler.Calls);
    }

    [Fact]
    public async Task AlreadyCancelledRequestDoesNotReadSecretOrSendHttp()
    {
        var secrets = new FakeSecrets();
        var handler = new FakeHandler((_, _) => Task.FromResult(Json("{}")));
        using var client = new HomeAssistantClient(Options.Create(ValidOptions()), secrets, handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.GetConfigAsync(cancellation.Token));
        Assert.Equal(Guid.Empty, secrets.RequestedId);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyReadUsesDeadlineAndCallerCancellation(bool cancelByCaller)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new WaitingStream(entered))
        }));
        var options = ValidOptions();
        options.Timeout = cancelByCaller ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(80);
        using var client = new HomeAssistantClient(Options.Create(options), new FakeSecrets(), handler);
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetConfigAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (cancelByCaller) cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => pending);
        Assert.True(cancelByCaller ? error is OperationCanceledException : error is HomeAssistantException);
        Assert.True(error!.InnerException is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedOrDeepResponseFailsClosed(bool deep)
    {
        var body = deep ? new string('[', 40) + "0" + new string(']', 40) : "{\"value\":\"" + new string('x', 1024 * 1024) + "\"}";
        using var client = new HomeAssistantClient(Options.Create(ValidOptions()), new FakeSecrets(),
            new FakeHandler((_, _) => Task.FromResult(Json(body))));
        var error = await Assert.ThrowsAsync<HomeAssistantException>(() => client.GetConfigAsync());
        Assert.Equal("A Home Assistant lekérés sikertelen.", error.Message);
    }

    private sealed class WaitingStream(TaskCompletionSource entered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HomeAssistantOptions ValidOptions() => new()
    {
        BaseUrl = "https://ha.example/",
        TokenSecretId = Guid.NewGuid().ToString(),
        DomainAllowlist = ["sensor", "light", "camera"],
        Timeout = TimeSpan.FromSeconds(2)
    };

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeSecrets : ISecretProvider
    {
        public string Token { get; } = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        public Guid RequestedId { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public Func<CancellationToken, Task<string>>? Read { get; set; }
        public Task<string> GetSecretAsync(Guid secretId, CancellationToken cancellationToken = default)
        {
            RequestedId = secretId;
            CancellationToken = cancellationToken;
            return Read is null ? Task.FromResult(Token) : Read(cancellationToken);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
