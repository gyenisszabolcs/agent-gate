using Microsoft.Extensions.DependencyInjection;
using AgentGate.Integrations.HomeAssistant.Control;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantControlRateLimitTests
{
    internal sealed class Clock : TimeProvider
    {
        public long Tick;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref Tick);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(-100);
    }
    [Fact]
    public void AtomicEntityAndGlobalReservationsUseMonotonicRollingWindow()
    {
        var clock = new Clock();
        var limiter = new ControlRateLimiter(clock, HomeAssistantControlPolicyTests.Policy());
        for (var i = 0; i < 6; i++) Assert.True(limiter.TryAcquire("light.a"));
        Assert.False(limiter.TryAcquire("light.a"));
        for (var i = 0; i < 54; i++) Assert.True(limiter.TryAcquire("light." + i));
        Assert.False(limiter.TryAcquire("light.fresh"));
        clock.Tick = 59999;
        Assert.False(limiter.TryAcquire("light.fresh"));
        clock.Tick = 60000;
        Assert.True(limiter.TryAcquire("light.fresh"));
        for (var i = 0; i < 5; i++) Assert.True(limiter.TryAcquire("light.fresh"));
        Assert.False(limiter.TryAcquire("light.fresh"));
    }
    [Fact]
    public async Task ConcurrentReservationsNeverOversubscribeOrLeakPartialQuota()
    {
        var limiter = new ControlRateLimiter(new Clock(), HomeAssistantControlPolicyTests.Policy());
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => limiter.TryAcquire("cover.room"))));
        Assert.Equal(6, results.Count(v => v));
        for (var i = 0; i < 54; i++) Assert.True(limiter.TryAcquire("light." + i));
        Assert.False(limiter.TryAcquire("lock.room"));
    }
    [Fact]
    public async Task FailedPostsExhaustEntityQuotaSharedWithApprovedWorker()
    {
        await using var f = new ControlFixture { Entity = "cover.room", Attributes = System.Text.Json.JsonSerializer.SerializeToElement(new { device_class = "blind" }) };
        f.Respond = (request, _) => request.Method == HttpMethod.Post ? throw new HttpRequestException() : Task.FromResult(f.Response(request));
        for (var i = 0; i < 6; i++)
        {
            using var failed = await f.Call("ha_control", "cover", "open_cover");
            Assert.True(ControlFixture.Failed(failed));
        }
        Assert.Equal(6, f.Posts);
        f.Attributes = System.Text.Json.JsonSerializer.SerializeToElement(new { device_class = "garage" });
        using var pending = await f.Call("ha_sensitive_control", "cover", "open_cover");
        Assert.False(ControlFixture.Failed(pending));
        Assert.Equal(AgentGate.Approval.ApprovalState.Failed, (await f.Channel.Approve()).State);
        Assert.Equal(6, f.Posts);
    }

    [Fact]
    public async Task ExhaustedControlQuotaReturnsSpecificSafeFailureWithoutAnotherPost()
    {
        await using var f = new ControlFixture(services => services.AddSingleton<TimeProvider>(new Clock())) { Entity = "light.room" };
        for (var i = 0; i < 6; i++)
        {
            using var success = await f.Call("ha_control", "light", "turn_on");
            Assert.False(ControlFixture.Failed(success));
        }
        using var failed = await f.Call("ha_control", "light", "turn_on");
        Assert.True(ControlFixture.Failed(failed));
        Assert.Equal(6, f.Posts);
        var text = failed.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.True(text == "A Home Assistant vezérlési gyakorisági korlát betelt.", "A konkrét, biztonságos gyakorisági hiba elveszett.");
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.App.AuditPath};Pooling=False");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT SanitizedError FROM AuditCalls ORDER BY rowid DESC LIMIT 1";
        Assert.True((string?)await command.ExecuteScalarAsync() == text, "A gyakorisági hiba auditja eltér.");
    }

    [Fact]
    public async Task ApprovedWorkerQuotaFailureReachesStatusOutcomeAndOriginalAuditWithoutPost()
    {
        await using var f = new ControlFixture(services => services.AddSingleton<TimeProvider>(new Clock()));
        using var pending = await f.Call("ha_sensitive_control", "lock", "unlock");
        Assert.False(ControlFixture.Failed(pending));
        var notice = Assert.Single(f.Channel.Requests);
        var limiter = f.App.Services.GetRequiredService<ControlRateLimiter>();
        for (var i = 0; i < 6; i++) Assert.True(limiter.TryAcquire("lock.room"));
        var outcome = await f.Channel.Approve();
        Assert.Equal(AgentGate.Approval.ApprovalState.Failed, outcome.State);
        Assert.Equal(0, f.Posts);
        const string expected = "A Home Assistant vezérlési gyakorisági korlát betelt.";
        Assert.True(outcome.Result == expected, "A konkrét gyakorisági hiba elveszett az eredményértesítésből.");
        var status = f.App.Services.GetRequiredService<AgentGate.Approval.ApprovalGate>().GetStatus(notice.Id);
        Assert.NotNull(status);
        Assert.Equal(AgentGate.Approval.ApprovalState.Failed, status.State);
        Assert.True(status.Result == expected, "A konkrét gyakorisági hiba elveszett a státuszból.");
        using var statusReply = await HomeAssistantToolProtocolTests.SendAsync(f.Client, "tools/call", new { name = "gate_approval_status", arguments = new { requestId = notice.Id.ToString() } });
        var statusText = statusReply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.True(statusText == "sikertelen: " + expected, "Az MCP státusz nem őrizte meg a konkrét gyakorisági hibát.");
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.App.AuditPath};Pooling=False");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT State, SanitizedError FROM AuditCalls WHERE CallId = $id";
        command.Parameters.AddWithValue("$id", notice.Id.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Failed", reader.GetString(0));
        Assert.True(reader.GetString(1) == expected, "Az eredeti Pending audit nem a konkrét hibával zárult.");
        await f.Channel.Decisions.Writer.WriteAsync(new(notice.Id, AgentGate.Approval.ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        Assert.Equal(0, f.Posts);
    }

    [Fact]
    public void MemoryCeilingFailsClosedAndExpiredEntityMetadataIsReclaimed()
    {
        var policy = HomeAssistantControlPolicyTests.Policy("{\"HomeAssistantControl\":{\"RateLimit\":{\"PerEntityPerMinute\":2147483647,\"GlobalPerMinute\":2147483647}}}");
        var clock = new Clock();
        var limiter = new ControlRateLimiter(clock, policy);
        for (var i = 0; i < 65536; i++) Assert.True(limiter.TryAcquire("light." + i));
        Assert.False(limiter.TryAcquire("light.new"));
        clock.Tick = 60000;
        Assert.True(limiter.TryAcquire("light.new"));
    }

    [Fact]
    public async Task ConcurrentRealControlAndApprovedWorkerShareOneGlobalReservation()
    {
        var policy = HomeAssistantControlPolicyTests.Policy("{\"HomeAssistantControl\":{\"Domains\":{\"light\":{\"AllowAllEntities\":true,\"ControlServices\":[\"turn_on\"]},\"lock\":{\"AllowAllEntities\":true,\"SensitiveControlServices\":[\"unlock\"]}},\"RateLimit\":{\"PerEntityPerMinute\":6,\"GlobalPerMinute\":2}}}");
        await using var f = new ControlFixture(services => services.AddSingleton(policy));
        f.Respond = (request, _) => Task.FromResult(request.Method == HttpMethod.Post ? f.Response(request) :
            new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    entity_id = request.RequestUri!.AbsolutePath.Split('/').Last(),
                    state = "on",
                    attributes = new { }
                }))
            });
        using var pending = await f.Call("ha_sensitive_control", "lock", "unlock");
        Assert.False(ControlFixture.Failed(pending));
        async Task<bool> Control()
        {
            using var reply = await f.Call("ha_control", "light", "turn_on");
            return !ControlFixture.Failed(reply);
        }
        var first = Control();
        var second = Control();
        var approved = f.Channel.Approve();
        var results = await Task.WhenAll(first, second);
        var outcome = await approved;
        Assert.Equal(2, f.Posts);
        Assert.Equal(2, results.Count(v => v) + (outcome.State == AgentGate.Approval.ApprovalState.Executed ? 1 : 0));
    }

    [Fact]
    public async Task PendingApprovalDoesNotReserveAndGlobalQuotaIsShared()
    {
        await using var f = new ControlFixture();
        using var pending = await f.Call("ha_sensitive_control", "lock", "unlock");
        f.Respond = (request, _) => request.Method == HttpMethod.Post ? throw new HttpRequestException() : Task.FromResult(f.Response(request));
        var service = f.App.Services.GetRequiredService<ControlRateLimiter>();
        for (var i = 0; i < 60; i++) Assert.True(service.TryAcquire("light." + i));
        Assert.Equal(AgentGate.Approval.ApprovalState.Failed, (await f.Channel.Approve()).State);
        Assert.Equal(0, f.Posts);
    }
}
