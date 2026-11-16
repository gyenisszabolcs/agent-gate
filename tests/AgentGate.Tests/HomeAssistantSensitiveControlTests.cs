using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using AgentGate.Approval;
using AgentGate.Integrations.HomeAssistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

internal sealed class ControlFixture : IAsyncDisposable
{
    public string Token { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public HomeAssistantReadToolsTests.RuntimeSecrets Secrets { get; } = new();
    public TestChannel Channel { get; } = new();
    public ConcurrentQueue<string> Calls { get; } = new();
    public string Entity { get; set; } = "lock.room";
    public JsonElement Attributes { get; set; } = JsonSerializer.SerializeToElement(new { device_class = "garage" });
    public JsonElement Config { get; set; } = JsonSerializer.SerializeToElement(new { unit_system = new { temperature = "°C" } });
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }
    public McpTestApplication App { get; }
    public HttpClient Client { get; }
    private readonly HomeAssistantReadToolsTests.Handler handler = new("{}");
    public int Posts => Calls.Count(c => c.StartsWith("POST", StringComparison.Ordinal));

    public ControlFixture(Action<IServiceCollection>? configure = null, string[]? allowedDomains = null)
    {
        handler.Respond = (request, ct) =>
        {
            Calls.Enqueue(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (Respond is not null) return Respond(request, ct);
            return Task.FromResult(Response(request));
        };
        App = new McpTestApplication(Token, services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton(HomeAssistantControlPolicyTests.Policy());
            services.AddSingleton<IApprovalChannel>(Channel);
            services.AddSingleton(_ => new HomeAssistantClient(Options.Create(new HomeAssistantOptions
            {
                BaseUrl = "https://ha.example/",
                TokenSecretId = Guid.NewGuid().ToString(),
                DomainAllowlist = allowedDomains ?? ["light", "switch", "cover", "climate", "scene", "input_boolean", "lock"]
            }), Secrets, handler));
            configure?.Invoke(services);
        });
        Client = HomeAssistantToolProtocolTests.CreateClient(App, Token);
    }
    public HttpResponseMessage Response(HttpRequestMessage request) => new(HttpStatusCode.OK)
    {
        Content = request.Method == HttpMethod.Post ? JsonContent.Create(new[] { new { entity_id = "lock.other", state = Secrets.Token } }) :
            request.RequestUri!.AbsolutePath == "/api/config" ? JsonContent.Create(Config) :
            JsonContent.Create(new { entity_id = Entity, state = "on", attributes = Attributes })
    };
    public Task<JsonDocument> Call(string tool, string domain, string service, string? data = null, string? entity = null) =>
        HomeAssistantToolProtocolTests.SendAsync(Client, "tools/call", new { name = tool, arguments = HomeAssistantControlPolicyTests.Arguments(domain, service, data, entity) });
    public static bool Failed(JsonDocument reply) => reply.RootElement.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();
    public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); handler.Dispose(); }

    internal sealed class TestChannel : IApprovalChannel
    {
        public string Name => "fake";
        public bool IsAvailable { get; set; } = true;
        public List<ApprovalNotice> Requests { get; } = [];
        public Channel<ApprovalDecision> Decisions { get; } = System.Threading.Channels.Channel.CreateUnbounded<ApprovalDecision>();
        public Channel<ApprovalOutcome> Outcomes { get; } = System.Threading.Channels.Channel.CreateUnbounded<ApprovalOutcome>();
        public Task SendRequestAsync(ApprovalNotice notice, CancellationToken ct) { Requests.Add(notice); return Task.CompletedTask; }
        public IAsyncEnumerable<ApprovalDecision> ReadDecisionsAsync(CancellationToken ct) => Decisions.Reader.ReadAllAsync(ct);
        public Task SendOutcomeAsync(ApprovalOutcome outcome, CancellationToken ct) => Outcomes.Writer.WriteAsync(outcome, ct).AsTask();
        public async Task<ApprovalOutcome> Approve()
        {
            var notice = Assert.Single(Requests);
            await Decisions.Writer.WriteAsync(new(notice.Id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
            return await Outcomes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

public sealed class HomeAssistantSensitiveControlTests
{
    [Theory]
    [InlineData("awning", false)]
    [InlineData("blind", false)]
    [InlineData("curtain", false)]
    [InlineData("damper", false)]
    [InlineData("shade", false)]
    [InlineData("shutter", false)]
    [InlineData("window", false)]
    [InlineData("garage", true)]
    [InlineData("gate", true)]
    [InlineData("door", true)]
    public async Task FreshCoverClassSeparatesRisksBeforePushAndBeforePost(string deviceClass, bool sensitive)
    {
        await using var f = new ControlFixture { Entity = "cover.room", Attributes = JsonSerializer.SerializeToElement(new { device_class = deviceClass }) };
        using var wrong = await f.Call(sensitive ? "ha_control" : "ha_sensitive_control", "cover", "open_cover");
        Assert.True(ControlFixture.Failed(wrong));
        Assert.Equal(0, f.Posts);
        Assert.Empty(f.Channel.Requests);
        using var right = await f.Call(sensitive ? "ha_sensitive_control" : "ha_control", "cover", "open_cover");
        Assert.False(ControlFixture.Failed(right));
        if (sensitive)
        {
            Assert.Equal(0, f.Posts);
            Assert.Equal(2, f.Calls.Count);
            Assert.Equal(ApprovalState.Executed, (await f.Channel.Approve()).State);
        }
        Assert.Equal(1, f.Posts);
        Assert.Equal(sensitive ? 5 : 4, f.Calls.Count);
    }

    [Theory]
    [InlineData("{\"device_class\":\"unknown\",\"device_class\":\"garage\"}")]
    [InlineData("{}")]
    [InlineData("{\"device_class\":null}")]
    [InlineData("{\"device_class\":12}")]
    [InlineData("{\"device_class\":\"unknown\"}")]
    public async Task UnknownCoverClassNeverPostsOrPushes(string attributes)
    {
        await using var f = new ControlFixture { Entity = "cover.room", Attributes = JsonDocument.Parse(attributes).RootElement.Clone() };
        foreach (var tool in new[] { "ha_control", "ha_sensitive_control" })
        {
            using var reply = await f.Call(tool, "cover", "open_cover");
            Assert.True(ControlFixture.Failed(reply));
        }
        Assert.Equal(0, f.Posts);
        Assert.Empty(f.Channel.Requests);
    }

    [Fact]
    public async Task CoverClassChangeAfterPushConsumesApprovalWithoutPost()
    {
        await using var f = new ControlFixture { Entity = "cover.room" };
        using var reply = await f.Call("ha_sensitive_control", "cover", "open_cover");
        Assert.False(ControlFixture.Failed(reply));
        Assert.Single(f.Calls);
        f.Attributes = JsonSerializer.SerializeToElement(new { device_class = "blind" });
        Assert.Equal(ApprovalState.Failed, (await f.Channel.Approve()).State);
        Assert.Equal(0, f.Posts);
    }

    [Theory]
    [InlineData("{\"entity_id\":\"cover.other\",\"state\":\"closed\",\"attributes\":{\"device_class\":\"garage\"}}")]
    [InlineData("{\"entity_id\":\"cover.room\",\"state\":12,\"attributes\":{\"device_class\":\"garage\"}}")]
    [InlineData("{\"entity_id\":\"cover.room\",\"state\":\"closed\",\"attributes\":[]}")]
    [InlineData("not-json")]
    public async Task InvalidOrWrongTargetFreshStateCannotSendSensitivePush(string body)
    {
        await using var f = new ControlFixture { Entity = "cover.room" };
        f.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        using var reply = await f.Call("ha_sensitive_control", "cover", "open_cover");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Empty(f.Channel.Requests);
        Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task LockStillRequiresExistingHaReadDomainPermission()
    {
        await using var f = new ControlFixture(allowedDomains: ["light"]);
        using var reply = await f.Call("ha_sensitive_control", "lock", "unlock");
        Assert.True(ControlFixture.Failed(reply));
        Assert.Empty(f.Calls);
        Assert.Empty(f.Channel.Requests);
        Assert.Equal(0, f.Secrets.Calls);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("unavailable")]
    [InlineData("kill")]
    public async Task ActualHaOperationCannotExecuteWithoutCurrentApprovalAndPolicy(string reason)
    {
        await using var f = new ControlFixture();
        if (reason == "unavailable") f.Channel.IsAvailable = false;
        using var reply = await f.Call("ha_sensitive_control", "lock", "unlock");
        if (reason == "unavailable")
        {
            Assert.True(ControlFixture.Failed(reply));
            Assert.Empty(f.Channel.Requests);
        }
        else
        {
            Assert.False(ControlFixture.Failed(reply));
            var notice = Assert.Single(f.Channel.Requests);
            if (reason == "kill") await File.WriteAllTextAsync(f.App.KillSwitchPath, "{\"Enabled\":true}");
            await f.Channel.Decisions.Writer.WriteAsync(new(notice.Id, reason == "rejected" ? ApprovalChoice.Reject : ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
            var outcome = await f.Channel.Outcomes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(reason == "rejected" ? ApprovalState.Rejected : ApprovalState.Failed, outcome.State);
        }
        Assert.Equal(0, f.Posts);
        Assert.Equal(0, f.Secrets.Calls);
    }

    [Fact]
    public async Task AsynchronousFreshPreparationHonorsCancellationWithoutPush()
    {
        await using var f = new ControlFixture { Entity = "cover.room" };
        f.Respond = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); };
        var handler = f.App.Services.GetServices<IApprovalOperationHandler>().Single(h => h.Tool == "ha_sensitive_control");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.PrepareAsync(HomeAssistantControlPolicyTests.Arguments("cover", "open_cover"), cts.Token).AsTask());
        Assert.Empty(f.Channel.Requests);
        Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task RealApprovalDispatchExecutesLockOnceAndReplayDoesNotPost()
    {
        await using var fixture = new ControlFixture();
        using var reply = await fixture.Call("ha_sensitive_control", "lock", "unlock");
        Assert.False(ControlFixture.Failed(reply));
        Assert.Equal(0, fixture.Posts);
        var notice = Assert.Single(fixture.Channel.Requests);
        Assert.Contains("lock.room", notice.Summary, StringComparison.Ordinal);
        var outcome = await fixture.Channel.Approve();
        Assert.Equal(ApprovalState.Executed, outcome.State);
        Assert.Equal(1, fixture.Posts);
        Assert.False(JsonSerializer.Serialize(outcome).Contains(fixture.Secrets.Token, StringComparison.Ordinal), "Titok a végállapotban.");
        await fixture.Channel.Decisions.Writer.WriteAsync(new(notice.Id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        using var status = await fixture.Call("ha_control", "light", "turn_off", entity: "light.denied");
        Assert.Equal(1, fixture.Posts);
    }
}
