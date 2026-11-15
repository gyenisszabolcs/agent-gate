using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentGate.Approval;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentGate.Audit;
using AgentGate.Policy;
using ModelContextProtocol.Server;

namespace AgentGate.Tests;

public sealed class ApprovalGateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineOrFailedSendRejectsWithoutExecuting(bool failSend)
    {
        var channel = new FakeChannel { IsAvailable = failSend, FailSend = failSend };
        await using var app = CreateApp(services => services.AddSingleton<IApprovalChannel>(channel));
        using var result = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        Assert.True(result.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(failSend ? 1 : 0, channel.Requests.Count);
        if (failSend)
            Assert.Null(app.Services.GetRequiredService<ApprovalGate>().GetStatus(channel.Requests[0].Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(161)]
    public async Task InvalidTestTextIsRejectedBeforePush(int length)
    {
        var channel = new FakeChannel();
        await using var app = CreateApp(services => services.AddSingleton<IApprovalChannel>(channel));
        using var result = await CallAsync(app, "gate_approval_test", new { text = new string('x', length) });
        Assert.True(result.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Empty(channel.Requests);
    }

    [Fact]
    public async Task FailedPendingCommitRevokesEarlyApprovalAndReplayWithoutIssuingPermission()
    {
        var channel = new FakeChannel();
        var handler = new ProbeHandler();
        var store = new BarrierStore { Fail = true };
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.AddSingleton<IAuditStore>(store);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        var call = CallAsync(app, "gate_approval_test", new { text = "első" });
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var id = Assert.Single(channel.Requests).Id;
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        store.Release.TrySetResult();
        using var failed = await call;
        Assert.True(failed.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(ApprovalState.Failed, app.Services.GetRequiredService<ApprovalGate>().GetStatus(id)!.State);
        var approvals = app.Services.GetRequiredService<InMemoryApprovalStore>();
        var permits = typeof(InMemoryApprovalStore).GetField("requests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(approvals)!;
        Assert.Equal(0, permits.GetType().GetProperty("Count")!.GetValue(permits));
        for (var index = 0; index < 8; index++)
            await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        store.Fail = false;
        using var next = await CallAsync(app, "gate_approval_test", new { text = "második" });
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(channel.Requests[1].Id, ApprovalChoice.Reject, "admin", DateTimeOffset.UtcNow));
        Assert.Equal(ApprovalState.Rejected, (await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2))).State);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ExpiryDuringExecutionAuditIsExpiredNotGenericFailure()
    {
        var clock = new MutableClock();
        var store = new AdvancingStore();
        var channel = new FakeChannel();
        var handler = new ProbeHandler();
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IAuditStore>(store);
            services.AddSingleton<IApprovalChannel>(channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        using var reply = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        var notice = Assert.Single(channel.Requests);
        store.Advance = () => clock.Now = notice.ExpiresAt;
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(notice.Id, ApprovalChoice.Approve, "admin", clock.Now));
        Assert.Equal(ApprovalState.Expired, (await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2))).State);
        Assert.Equal(0, handler.Count);
        Assert.True(store.ExpiredRecorded);
    }

    [Fact]
    public async Task RepeatedStopAfterDisposeIsSafeForAliasedHostedSingleton()
    {
        await using var app = CreateApp();
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        gate.Dispose();
        await gate.StopAsync(CancellationToken.None);
        gate.Dispose();
    }

    [Fact]
    public async Task TimedOutChannelWorkRemainsOwnedAndCannotAccumulateMoreSends()
    {
        var channel = new FakeChannel { BlockSend = true };
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.PostConfigure<ApprovalGateOptions>(options => options.SendTimeoutSeconds = 1);
        });
        using var first = await CallAsync(app, "gate_approval_test", new { text = "első" });
        using var second = await CallAsync(app, "gate_approval_test", new { text = "második" });
        var countWhileBlocked = channel.Requests.Count;
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await app.Services.GetRequiredService<ApprovalGate>().StopAsync(stop.Token);
        channel.SendRelease.TrySetResult();
        Assert.Equal(1, countWhileBlocked);
    }

    [Fact]
    public async Task DecisionReaderFailureIsSanitizedAndDisablesNewRequests()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var channel = new FakeChannel { ReaderFailure = secret };
        var logs = new CaptureLogs();
        await using var app = CreateApp(services =>
        {
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddSingleton<IApprovalChannel>(channel);
        });
        using var first = await CallAsync(app, "gate_approval_test", new { text = "első" });
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(Guid.NewGuid(), ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        await channel.ReaderFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        using var next = await CallAsync(app, "gate_approval_test", new { text = "második" });
        var result = next.RootElement.GetProperty("result");
        Assert.True(result.TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.Single(channel.Requests);
        Assert.True(!logs.Contains(secret), "Nyers csatornakivétel a diagnosztikában.");
    }

    [Theory]
    [InlineData("deferred_sensitive", RiskLevel.SensitiveControl)]
    [InlineData("deferred_configuration", RiskLevel.Configuration)]
    [InlineData("deferred_system", RiskLevel.System)]
    public async Task EveryHighRiskUsesSameStoredGateAndConcurrentApprovalExecutesExactlyOnce(string tool, RiskLevel risk)
    {
        var channel = new FakeChannel();
        var handler = new ProbeHandler { Tool = tool, Risk = risk };
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
            services.AddMcpServer().WithTools<DeferredTools>();
        });
        using (var reply = await CallAsync(app, tool, new { text = " első " })) { }
        var id = Assert.Single(channel.Requests).Id;
        Assert.Equal(0, handler.Count);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => channel.Decisions.Writer.WriteAsync(
            new ApprovalDecision(id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow)).AsTask()));
        var outcome = await channel.Outcomes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ApprovalState.Executed, outcome.State);
        Assert.Equal(1, handler.Count);
        Assert.Equal("első", handler.Last!.Parameters["text"]);
        using (var reply = await CallAsync(app, tool, new { text = "második" })) { }
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(channel.Requests[1].Id, ApprovalChoice.Reject, "admin", DateTimeOffset.UtcNow));
        var second = await channel.Outcomes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ApprovalState.Rejected, second.State);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("decision", false, 0)]
    [InlineData("execution", false, 0)]
    [InlineData("execution", true, 1)]
    [InlineData("outcome", true, 1)]
    public async Task RealSqliteAuditFailuresFailClosedWithoutRollingBackOrRetrying(string phase, bool complete, int expectedExecutions)
    {
        var channel = new FakeChannel();
        var handler = new ProbeHandler();
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        using var reply = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={app.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = complete
            ? $"CREATE TRIGGER fail_audit BEFORE UPDATE ON AuditCalls WHEN OLD.SanitizedParameters LIKE '%{phase}%' BEGIN SELECT RAISE(ABORT, 'Tesztelt audithiba.'); END;"
            : $"CREATE TRIGGER fail_audit BEFORE INSERT ON AuditCalls WHEN NEW.SanitizedParameters LIKE '%{phase}%' BEGIN SELECT RAISE(ABORT, 'Tesztelt audithiba.'); END;";
        await command.ExecuteNonQueryAsync();
        var id = Assert.Single(channel.Requests).Id;
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        Assert.Equal(ApprovalState.Failed, (await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2))).State);
        Assert.Equal(expectedExecutions, handler.Count);
        if (complete)
        {
            command.CommandText = "SELECT COUNT(*) FROM AuditCalls WHERE State = 'Started'";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task SuccessfulResultAndSummaryAreFilteredInStatusLifecycleAuditAndStructuredLogs()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var channel = new FakeChannel();
        var logs = new CaptureLogs();
        var handler = new ProbeHandler { Result = "token=" + secret };
        await using var app = CreateApp(services =>
        {
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddSingleton<IApprovalChannel>(channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        using var pending = await CallAsync(app, "gate_approval_test", new { text = "token=" + secret });
        var notice = Assert.Single(channel.Requests);
        Assert.True(!JsonSerializer.Serialize(notice).Contains(secret, StringComparison.Ordinal), "Titok az összefoglalóban.");
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(notice.Id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        var outcome = await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(outcome.Result == "token=[REDACTED]", "A sikeres eredmény szűrése hibás.");
        using var status = await CallAsync(app, "gate_approval_status", new { requestId = notice.Id.ToString() });
        Assert.True(!status.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "Titok a státuszban.");
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={app.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SanitizedParameters, SanitizedError FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        var resultRecorded = false;
        while (await reader.ReadAsync())
        {
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = reader.IsDBNull(index) ? "" : reader.GetString(index);
                Assert.True(!value.Contains(secret, StringComparison.Ordinal), "Titok a tartós auditban.");
                resultRecorded |= value.Contains("outcome", StringComparison.Ordinal) && value.Contains("REDACTED", StringComparison.Ordinal);
            }
        }
        Assert.True(resultRecorded, "A szűrt végrehajtási eredmény hiányzik az auditból.");
        Assert.True(!logs.Contains(secret), "Titok a strukturált diagnosztikában.");
    }

    [Fact]
    public async Task HashTamperingBlocksExecutionAndNeverIssuesPermissionForPendingRequests()
    {
        var channel = new FakeChannel();
        var handler = new ProbeHandler();
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        using var reply = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        var id = Assert.Single(channel.Requests).Id;
        var permissions = app.Services.GetRequiredService<InMemoryApprovalStore>();
        var permits = typeof(InMemoryApprovalStore).GetField("requests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(permissions)!;
        Assert.Equal(0, permits.GetType().GetProperty("Count")!.GetValue(permits));
        var gate = app.Services.GetRequiredService<ApprovalGate>();
        var requests = typeof(ApprovalGate).GetField("requests", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(gate)!;
        var pending = requests.GetType().GetProperty("Item")!.GetValue(requests, [id])!;
        pending.GetType().GetProperty("Hash")!.SetValue(pending, new string('0', 64));
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        Assert.Equal(ApprovalState.Failed, (await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2))).State);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ForeignUnknownAndRestartedRequestsHaveIdenticalStatusAndClientCallerCannotOverrideOwner()
    {
        var channel = new FakeChannel();
        await using var app = CreateApp(services => services.AddSingleton<IApprovalChannel>(channel));
        using var reply = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        var id = Assert.Single(channel.Requests).Id;
        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuditOptions>>().Value;
        using var own = await CallAsync(app, "gate_approval_status", new { requestId = id.ToString() });
        Assert.Equal("várakozik", own.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        options.Caller = "other";
        using var foreign = await CallAsync(app, "gate_approval_status", new { requestId = id.ToString(), caller = "test-caller", agent = "test-caller" });
        using var unknown = await CallAsync(app, "gate_approval_status", new { requestId = Guid.NewGuid().ToString() });
        Assert.Equal(unknown.RootElement.GetProperty("result").GetRawText(), foreign.RootElement.GetProperty("result").GetRawText());
        await using var restarted = CreateApp();
        using var lost = await CallAsync(restarted, "gate_approval_status", new { requestId = id.ToString() });
        Assert.Equal(unknown.RootElement.GetProperty("result").GetRawText(), lost.RootElement.GetProperty("result").GetRawText());
    }

    [Fact]
    public async Task KillSwitchIsRecheckedAfterApprovalAndBeforeExecution()
    {
        var channel = new FakeChannel();
        var handler = new ProbeHandler();
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        using var reply = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        await File.WriteAllTextAsync(app.KillSwitchPath, "{\"Enabled\":true}");
        var id = Assert.Single(channel.Requests).Id;
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        Assert.Equal(ApprovalState.Failed, (await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2))).State);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task PreparedOperationCannotChangeMatchedToolOrRisk()
    {
        var channel = new FakeChannel();
        var handler = new ProbeHandler { WrongIdentity = true };
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        using var reply = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        Assert.True(reply.RootElement.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean());
        Assert.Empty(channel.Requests);
    }

    [Fact]
    public async Task ExecutionExceptionClosesDurableAuditAndNeverRetriesOrLeaksRawFailure()
    {
        var channel = new FakeChannel();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var logs = new CaptureLogs();
        var handler = new ProbeHandler { Error = secret };
        await using var app = CreateApp(services =>
        {
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddSingleton<IApprovalChannel>(channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        using var pending = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        var id = Assert.Single(channel.Requests).Id;
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => channel.Decisions.Writer.WriteAsync(
            new ApprovalDecision(id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow)).AsTask()));
        var outcome = await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ApprovalState.Failed, outcome.State);
        Assert.True(!JsonSerializer.Serialize(outcome).Contains(secret, StringComparison.Ordinal), "Titok a kimenetben.");
        Assert.Equal(1, handler.Count);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={app.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State, SanitizedError FROM AuditCalls WHERE SanitizedParameters LIKE '%execution%'";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Failed", reader.GetString(0));
        Assert.True(!reader.GetString(1).Contains(secret, StringComparison.Ordinal), "Titok az auditban.");
        Assert.True(!logs.Contains(secret), "Titok a strukturált hibanaplóban.");
    }

    [Fact]
    public async Task OutcomeDeliveryFailureDoesNotKillDecisionReaderOrRetryExecution()
    {
        var channel = new FakeChannel { FailOutcome = true };
        var handler = new ProbeHandler();
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        using var first = await CallAsync(app, "gate_approval_test", new { text = "első" });
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(channel.Requests[0].Id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        channel.FailOutcome = false;
        using var second = await CallAsync(app, "gate_approval_test", new { text = "második" });
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(channel.Requests[1].Id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        var limit = Task.Delay(TimeSpan.FromSeconds(2));
        while (handler.Count < 2 && !limit.IsCompleted) await Task.Delay(10);
        Assert.Equal(2, handler.Count);
        Assert.Equal(ApprovalState.Executed, app.Services.GetRequiredService<ApprovalGate>().GetStatus(channel.Requests[0].Id)!.State);
    }

    [Fact]
    public async Task LifetimeIsConfiguredAndInvalidOptionsFailStartupSafely()
    {
        var channel = new FakeChannel();
        var clock = new MutableClock();
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<ApprovalGateOptions>(options => options.LifetimeMinutes = 2);
        });
        using var pending = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        Assert.Equal(TimeSpan.FromMinutes(2), Assert.Single(channel.Requests).ExpiresAt - clock.Now);
        await using var invalid = CreateApp(services => services.PostConfigure<ApprovalGateOptions>(options => options.LifetimeMinutes = 0));
        var exception = Record.Exception(() => invalid.CreateClient());
        Assert.NotNull(exception);
        Assert.Contains("Érvénytelen jóváhagyási konfiguráció", exception.ToString());
    }

    [Fact]
    public async Task NonCooperativeSendHasBoundedDeadlineAndCannotGrantPermission()
    {
        var channel = new FakeChannel { BlockSend = true };
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.PostConfigure<ApprovalGateOptions>(options => options.SendTimeoutSeconds = 1);
        });
        var call = CallAsync(app, "gate_approval_test", new { text = "próba" });
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(3)));
        channel.SendRelease.TrySetResult();
        using var result = await call;
        Assert.Same(call, finished);
        Assert.True(result.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        var id = Assert.Single(channel.Requests).Id;
        Assert.Null(app.Services.GetRequiredService<ApprovalGate>().GetStatus(id));
    }

    [Fact]
    public async Task EarlyDecisionCannotExecuteBeforePendingAuditCommits()
    {
        var channel = new FakeChannel();
        var handler = new ProbeHandler();
        var store = new BarrierStore();
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.AddSingleton<IAuditStore>(store);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        var call = CallAsync(app, "gate_approval_test", new { text = "próba" });
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var id = Assert.Single(channel.Requests).Id;
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        await Task.WhenAny(handler.Executed.Task, Task.Delay(200));
        var countBeforeCommit = handler.Count;
        store.Release.TrySetResult();
        using var reply = await call;
        await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, countBeforeCommit);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecisionAndExpiryAreCorrelatedDurablyWithoutClaimingPendingExecutionSucceeded(bool expire)
    {
        var channel = new FakeChannel();
        var clock = new MutableClock();
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.AddSingleton<TimeProvider>(clock);
        });
        using var pending = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        var notice = Assert.Single(channel.Requests);
        if (expire)
            clock.Now = notice.ExpiresAt;
        else
            await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(notice.Id, ApprovalChoice.Reject, "admin", clock.Now));
        await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={app.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SanitizedParameters FROM AuditCalls WHERE Tool = 'approval_lifecycle'";
        var records = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) records.Add(reader.GetString(0));
        Assert.Contains(records, text => text.Contains(notice.Id.ToString(), StringComparison.Ordinal)
            && text.Contains(expire ? "expired" : "decision", StringComparison.Ordinal)
            && (expire || (text.Contains("admin", StringComparison.Ordinal) && text.Contains("fake", StringComparison.Ordinal))));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ExpiryBoundaryUsesGateClockAndOfflineRequestsExpire(long ticksBeforeExpiry, bool expired)
    {
        var channel = new FakeChannel();
        var clock = new MutableClock();
        var handler = new ProbeHandler();
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        using var pending = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        var notice = Assert.Single(channel.Requests);
        Assert.Equal(TimeSpan.FromMinutes(15), notice.ExpiresAt - clock.Now);
        clock.Now = notice.ExpiresAt.AddTicks(-ticksBeforeExpiry);
        if (expired)
            channel.IsAvailable = false;
        else
            await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(notice.Id, ApprovalChoice.Approve, "admin", clock.Now));
        var outcome = await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(expired ? ApprovalState.Expired : ApprovalState.Executed, outcome.State);
        Assert.Equal(expired ? 0 : 1, handler.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectOrInvalidDecisionNeverExecutesAndReportsRejected(bool invalid)
    {
        var channel = new FakeChannel();
        var handler = new ProbeHandler();
        await using var app = CreateApp(services =>
        {
            services.AddSingleton<IApprovalChannel>(channel);
            services.RemoveAll<IApprovalOperationHandler>();
            services.AddSingleton<IApprovalOperationHandler>(handler);
        });
        using var pending = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        var id = Assert.Single(channel.Requests).Id;
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(id, invalid ? (ApprovalChoice)42 : ApprovalChoice.Reject, "admin", DateTimeOffset.UtcNow));
        var outcome = await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ApprovalState.Rejected, outcome.State);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task ApprovalExecutesStoredOperationAfterRequestEndsAndStatusReportsActualOutcome()
    {
        var channel = new FakeChannel();
        await using var app = CreateApp(services => services.AddSingleton<IApprovalChannel>(channel));
        using (var pending = await CallAsync(app, "gate_approval_test", new { text = "próba" })) { }
        var id = Assert.Single(channel.Requests).Id;
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        var outcome = await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ApprovalState.Executed, outcome.State);
        using var status = await CallAsync(app, "gate_approval_status", new { requestId = id.ToString() });
        var text = status.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("végrehajtva", text);
    }

    [Fact]
    public async Task HighRiskCallReturnsPendingWithoutExecutingAndSendsOnlySafeSummary()
    {
        var channel = new FakeChannel();
        await using var app = CreateApp(services => services.AddSingleton<IApprovalChannel>(channel));
        using var result = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        var reply = result.RootElement.GetProperty("result");
        Assert.False(reply.TryGetProperty("isError", out var error) && error.GetBoolean());
        var text = reply.GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("jóváhagyásra vár", text);
        Assert.Single(channel.Requests);
        Assert.Contains(channel.Requests[0].Id.ToString(), text);
        Assert.Equal("gate_approval_test", channel.Requests[0].Tool);
        using var summary = JsonDocument.Parse(channel.Requests[0].Summary);
        Assert.Equal("próba", summary.RootElement.GetProperty("text").GetString());
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={app.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM AuditCalls WHERE Tool = 'gate_approval_test'";
        Assert.Equal("Pending", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ProgramExposesApprovalTestAndRejectsWithoutChannel()
    {
        await using var app = CreateApp();
        using var list = await SendAsync(app, "tools/list", new { });
        Assert.Contains(list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "gate_approval_test");
        using var result = await CallAsync(app, "gate_approval_test", new { text = "próba" });
        Assert.True(result.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    private static McpTestApplication CreateApp(Action<IServiceCollection>? configure = null) => new(
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            configure?.Invoke(services);
        });

    private static Task<JsonDocument> CallAsync(McpTestApplication app, string name, object arguments) =>
        SendAsync(app, "tools/call", new { name, arguments });

    private static async Task<JsonDocument> SendAsync(McpTestApplication app, string method, object parameters)
    {
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentGate.Policy.McpAuthenticationOptions>>().Value.Token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method, @params = parameters }, deadline.Token);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            body = body.Split('\n').Last(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        }
        return JsonDocument.Parse(body);
    }

    private sealed class FakeChannel : IApprovalChannel
    {
        public bool IsAvailable { get; set; } = true;
        public string Name => "fake";
        public bool BlockSend { get; set; }
        public bool FailOutcome { get; set; }
        public bool FailSend { get; set; }
        public string? ReaderFailure { get; set; }
        public TaskCompletionSource ReaderFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SendRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<ApprovalNotice> Requests { get; } = [];
        public Channel<ApprovalDecision> Decisions { get; } = Channel.CreateUnbounded<ApprovalDecision>();
        public Channel<ApprovalOutcome> Outcomes { get; } = Channel.CreateUnbounded<ApprovalOutcome>();
        public TaskCompletionSource<ApprovalOutcome> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendRequestAsync(ApprovalNotice request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (FailSend) throw new InvalidOperationException("A próbaküldés sikertelen.");
            return BlockSend ? SendRelease.Task : Task.CompletedTask;
        }
        public async IAsyncEnumerable<ApprovalDecision> ReadDecisionsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var decision in Decisions.Reader.ReadAllAsync(cancellationToken))
            {
                if (ReaderFailure is not null)
                {
                    ReaderFailed.TrySetResult();
                    throw new InvalidOperationException(ReaderFailure);
                }
                yield return decision;
            }
        }
        public Task SendOutcomeAsync(ApprovalOutcome outcome, CancellationToken cancellationToken)
        {
            Finished.TrySetResult(outcome);
            Outcomes.Writer.TryWrite(outcome);
            if (FailOutcome) throw new InvalidOperationException("Nem kézbesíthető.");
            return Task.CompletedTask;
        }
    }

    private sealed class ProbeHandler : IApprovalOperationHandler
    {
        public string Tool { get; init; } = "gate_approval_test";
        public RiskLevel Risk { get; init; } = RiskLevel.SensitiveControl;
        public int Count;
        public string? Error { get; set; }
        public string Result { get; set; } = "végrehajtva";
        public bool WrongIdentity { get; set; }
        public PreparedOperation? Last { get; private set; }
        public TaskCompletionSource Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PreparedOperation Prepare(IReadOnlyDictionary<string, JsonElement> arguments) => WrongIdentity
            ? new PreparedOperation("other", AgentGate.Policy.RiskLevel.Read, new Dictionary<string, string?>())
            : new PreparedOperation(Tool, Risk, new ApprovalTestOperation().Prepare(arguments).Parameters);
        public Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            Last = operation;
            Executed.TrySetResult();
            if (Error is not null) throw new InvalidOperationException(Error);
            return Task.FromResult(Result);
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 16, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BarrierStore : IAuditStore
    {
        public bool Fail { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task BeginAsync(AuditCall call, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task CompleteAsync(Guid callId, string state, double durationMilliseconds, string? sanitizedError, CancellationToken cancellationToken)
        {
            if (state == "Pending")
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                if (Fail) throw new InvalidOperationException("A Pending audit nem véglegesíthető.");
            }
        }
    }

    private sealed class AdvancingStore : IAuditStore
    {
        public Action? Advance { get; set; }
        public bool ExpiredRecorded { get; private set; }
        public Task BeginAsync(AuditCall call, CancellationToken cancellationToken)
        {
            using var parameters = JsonDocument.Parse(call.SanitizedParameters);
            if (parameters.RootElement.TryGetProperty("phase", out var phase))
            {
                if (phase.GetString() == "execution") Advance?.Invoke();
                if (phase.GetString() == "expired") ExpiredRecorded = true;
            }
            return Task.CompletedTask;
        }
        public Task CompleteAsync(Guid callId, string state, double durationMilliseconds, string? sanitizedError, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> entries = new();
        public ILogger CreateLogger(string categoryName) => new Capture(entries);
        public bool Contains(string value) => entries.Any(entry => entry.Contains(value, StringComparison.Ordinal));
        public void Dispose() { }
        private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(formatter(state, exception) + (state is IEnumerable<KeyValuePair<string, object?>> values
                    ? string.Join(";", values.Select(pair => pair.Key + "=" + pair.Value)) : state?.ToString()) + exception);
        }
    }

    [McpServerToolType]
    public sealed class DeferredTools
    {
        [McpServerTool(Name = "deferred_sensitive"), ToolRisk(RiskLevel.SensitiveControl), ToolAuditParameters("text")]
        public static string Sensitive(string text) => throw new InvalidOperationException("Az eredeti MCP-delegált nem futhat.");
        [McpServerTool(Name = "deferred_configuration"), ToolRisk(RiskLevel.Configuration), ToolAuditParameters("text")]
        public static string Configuration(string text) => throw new InvalidOperationException("Nincs valódi konfigurációmódosítás.");
        [McpServerTool(Name = "deferred_system"), ToolRisk(RiskLevel.System), ToolAuditParameters("text")]
        public static string SystemOperation(string text) => throw new InvalidOperationException("Az eredeti MCP-delegált nem futhat.");
    }
}
