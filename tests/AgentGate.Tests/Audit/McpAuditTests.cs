using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AgentGate.Audit;
using AgentGate.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentGate.Tests.Audit;

public sealed class McpAuditTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RealSdkErrorTypeBoundaryIsPersistedWithIdenticalSafeText(int kind)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools { Payload = "Biztonságos részlet token=" + secret, FailureKind = kind };
        await using var factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(probe.Logs));
            services.AddMcpServer().WithTools(probe);
        });
        using var response = await CallAsync(factory, kind < 0 ? "safe-failure" : "unmarked", new { });
        Assert.True(IsError(response));
        Assert.Equal(1, probe.Count);
        var text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        var expected = kind < 0 ? "Biztonságos részlet token=[REDACTED]" : "Az eszköz végrehajtása sikertelen.";
        Assert.True(text == expected, "A valódi SDK-hívás típusbizalmi határa hibás.");
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State, SanitizedError, SanitizedParameters FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Failed", reader.GetString(0));
        Assert.True(reader.GetString(1) == text, "A tartós audit és a válasz hibaszövege eltér.");
        Assert.True(!reader.GetString(1).Contains(secret, StringComparison.Ordinal), "A tartós audit titkot tartalmaz.");
        Assert.True(!reader.GetString(2).Contains(secret, StringComparison.Ordinal), "A tartós paraméterek titkot tartalmaznak.");
        Assert.False(await reader.ReadAsync());
        Assert.True(!response.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "A válasz titkot tartalmaz.");
        Assert.True(!probe.Logs.Contains(secret), "A napló titkot tartalmaz.");
    }

    [Fact]
    public async Task MarkedErrorGrowingPastRedactorLimitKeepsIdenticalOnceRedactedReplyAndDurableAudit()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var message = string.Concat(Enumerable.Repeat("token=x ", 8_000)) + "token=" + secret;
        var expected = AgentGate.Secrets.SecretRedactor.RedactText(message)!;
        Assert.True(message.Length < 65_536, "A bemenetnek a szűrő méretkorlátja alatt kell lennie.");
        Assert.True(expected.Length > 65_536, "Az egyszer szűrt kimenetnek a méretkorlát fölé kell nőnie.");
        Assert.True(expected == string.Concat(Enumerable.Repeat("token=[REDACTED] ", 8_000)) + "token=[REDACTED]",
            "Az egyszeri szűrés nem a várt szöveget adta.");
        var probe = new ProbeTools { Payload = message };
        await using var factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(probe.Logs));
            services.AddMcpServer().WithTools(probe);
        });
        using var response = await CallAsync(factory, "safe-failure", new { });
        Assert.True(IsError(response));
        Assert.True(probe.Count == 1, "Az eszköznek pontosan egyszer kell lefutnia.");
        var text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.True(text == expected, "A válasz nem az egyszer szűrt hibaszöveg.");
        Assert.True(!response.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "A válasz titkot tartalmaz.");
        Assert.True(!probe.Logs.Contains(secret), "A napló titkot tartalmaz.");
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State, SanitizedError, SanitizedParameters FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetString(0) == "Failed", "A tartós audit állapota nem Failed.");
        for (var index = 0; index < reader.FieldCount; index++)
        {
            Assert.True(!reader.GetString(index).Contains(secret, StringComparison.Ordinal), "A tartós audit titkot tartalmaz.");
        }
        Assert.True(reader.GetString(1) == expected, "A tartós audit nem az egyszer szűrt hibaszöveg.");
        Assert.True(reader.GetString(1) == text, "A tartós audit és a válasz hibaszövege eltér.");
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task DurableBeginPrecedesFreshFileReadAndRejectedCallIsDurablyFailed()
    {
        var probe = new ProbeTools();
        var observedStarted = false;
        McpTestApplication? factory = null;
        factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddMcpServer().WithTools(probe);
            services.AddSingleton<IKillSwitchStateSource>(new FileKillSwitchStateSource(
                Microsoft.Extensions.Options.Options.Create(new KillSwitchFileOptions { Path = factory!.KillSwitchPath }), async (path, token) =>
                {
                    await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
                    await connection.OpenAsync(token);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT COUNT(*) FROM AuditCalls WHERE State = 'Started' AND Tool = 'write'";
                    observedStarted = (long)(await command.ExecuteScalarAsync(token))! == 1;
                    await File.WriteAllTextAsync(path, "{\"Enabled\":true}", token);
                    return await File.ReadAllBytesAsync(path, token);
                }));
        });
        await using var lifetime = factory;
        using var response = await CallAsync(factory, "write", new { });
        Assert.True(observedStarted, "A kapcsolóolvasás előtt tartós Started audit szükséges.");
        Assert.True(IsError(response));
        Assert.Equal(0, probe.Count);
        await using var reopened = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
        await reopened.OpenAsync();
        await using var read = reopened.CreateCommand();
        read.CommandText = "SELECT State, SanitizedError FROM AuditCalls";
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Failed", reader.GetString(0));
        Assert.Equal("A vészleállító policy letiltotta az eszköz végrehajtását.", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task BeginFailureDoesNotReadSwitch()
    {
        var probe = new ProbeTools();
        var store = new RecordingStore { BeginError = "Tesztírási hiba." };
        var reads = 0;
        await using var factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton<IAuditStore>(store);
            services.AddMcpServer().WithTools(probe);
            services.AddSingleton<IKillSwitchStateSource>(new FileKillSwitchStateSource(
                Microsoft.Extensions.Options.Options.Create(new KillSwitchFileOptions()), (_, _) =>
                {
                    reads++;
                    return Task.FromResult(System.Text.Encoding.UTF8.GetBytes("{\"Enabled\":false}"));
                }));
        });
        using var response = await CallAsync(factory, "write", new { });
        Assert.True(IsError(response));
        Assert.Equal(0, reads);
        Assert.Equal(0, probe.Count);
    }

    [Fact]
    public async Task CancellationDuringGateFileReadNeverStartsToolAndCompletesAudit()
    {
        var probe = new ProbeTools();
        var store = new RecordingStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancellation = false;
        await using var factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton<IAuditStore>(store);
            services.AddMcpServer().WithTools(probe);
            services.AddSingleton<IKillSwitchStateSource>(new FileKillSwitchStateSource(
                Microsoft.Extensions.Options.Options.Create(new KillSwitchFileOptions()), async (_, token) =>
                {
                    entered.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    catch (OperationCanceledException)
                    {
                        observedCancellation = token.IsCancellationRequested;
                        throw;
                    }
                    return [];
                }));
        });
        using var cancellation = new CancellationTokenSource();
        var pending = CallAsync(factory, "write", new { }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(observedCancellation);
        Assert.Equal(0, probe.Count);
        Assert.Equal("Cancelled", Assert.Single(store.Calls).State);
        Assert.True(store.CompletionTokenCanBeCancelled);
    }

    [Theory]
    [InlineData("count", RiskLevel.Read)]
    [InlineData("write", RiskLevel.Control)]
    [InlineData("sensitive", RiskLevel.SensitiveControl)]
    [InlineData("configure", RiskLevel.Configuration)]
    [InlineData("system", RiskLevel.System)]
    [InlineData("forbidden", RiskLevel.Forbidden)]
    [InlineData("invalid-risk", RiskLevel.Forbidden)]
    public async Task AtomicSwitchReplacementAppliesToEveryRiskWithoutRestart(string tool, RiskLevel risk)
    {
        var probe = new ProbeTools();
        var store = new RecordingStore();
        await using var factory = CreateFactory(probe, store);
        _ = factory.Services;
        string?[] states = ["{\"Enabled\":false}", "{\"Enabled\":true}", "{\"Enabled\":false}", "hibás", "{\"Enabled\":false}", null, "{\"Enabled\":false}", "[könyvtár]", "{\"Enabled\":false}"];
        var executed = 0;
        for (var index = 0; index < states.Length; index++)
        {
            if (states[index] == "[könyvtár]")
            {
                File.Delete(factory.KillSwitchPath);
                Directory.CreateDirectory(factory.KillSwitchPath);
            }
            else if (states[index] is { } json)
            {
                if (Directory.Exists(factory.KillSwitchPath))
                {
                    Directory.Delete(factory.KillSwitchPath);
                }
                var replacement = factory.KillSwitchPath + ".new";
                await File.WriteAllTextAsync(replacement, json);
                File.Move(replacement, factory.KillSwitchPath, overwrite: true);
            }
            else
            {
                File.Delete(factory.KillSwitchPath);
            }
            using var result = await CallAsync(factory, tool, new { });
            var requiresApproval = risk is RiskLevel.SensitiveControl or RiskLevel.Configuration or RiskLevel.System;
            var allowed = risk == RiskLevel.Read || (risk == RiskLevel.Control && states[index] == "{\"Enabled\":false}");
            Assert.True(IsError(result) == !allowed, "A kapcsoló friss állapota nem érvényesült.");
            executed += allowed ? 1 : 0;
            Assert.Equal(executed, probe.Count);
            Assert.Equal(allowed ? "Succeeded" : "Failed", store.Calls[index].State);
            Assert.Equal(risk, store.Calls[index].RiskLevel);
            if (!allowed && tool != "invalid-risk" && !(requiresApproval && states[index] == "{\"Enabled\":false}"))
            {
                var text = result.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
                Assert.True(text == "A vészleállító policy letiltotta az eszköz végrehajtását.", "A tiltás indoka nem konstans.");
                Assert.True(text == store.Calls[index].SanitizedError, "A válasz és az audit indoka eltér.");
            }
        }
        Assert.Equal(states.Length, store.Calls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task UnmarkedFailuresWithIdenticalTextOrWrappedMarkerRemainGeneric(int kind)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools { Payload = "Biztonságos részlet token=" + secret, FailureKind = kind };
        var store = new RecordingStore();
        await using var factory = CreateFactory(probe, store);
        using var response = await CallAsync(factory, "unmarked", new { });
        Assert.True(IsError(response));
        Assert.Equal(1, probe.Count);
        var text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.True(text == "Az eszköz végrehajtása sikertelen.", "A jelöletlen hiba nem általános.");
        var call = Assert.Single(store.Calls);
        Assert.Equal("Failed", call.State);
        Assert.True(call.SanitizedError == text, "A válasz és az audit hibaszövege eltér.");
        Assert.True(!JsonSerializer.Serialize(call).Contains(secret, StringComparison.Ordinal), "Az audit titkot tartalmaz.");
        Assert.True(!response.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "A válasz titkot tartalmaz.");
        Assert.True(!probe.Logs.Contains(secret), "A napló titkot tartalmaz.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  Magyar részlet.\nMásodik sor!  ")]
    public async Task SafeMessageIsOtherwiseUnchanged(string message)
    {
        var probe = new ProbeTools { Payload = message };
        var store = new RecordingStore();
        await using var factory = CreateFactory(probe, store);
        using var response = await CallAsync(factory, "safe-failure", new { });
        Assert.True(IsError(response));
        var text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.True(text == message, "A biztonságos szöveg megváltozott.");
        Assert.True(Assert.Single(store.Calls).SanitizedError == text, "A válasz és az audit hibaszövege eltér.");
    }

    [Fact]
    public async Task MarkedToolExceptionPreservesOnlyRedactedMessageThroughRealSdk()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools { Payload = "Biztonságos részlet token=" + secret };
        var store = new RecordingStore();
        await using var factory = CreateFactory(probe, store);
        using var response = await CallAsync(factory, "safe-failure", new { });
        Assert.True(IsError(response));
        Assert.Equal(1, probe.Count);
        var text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.True(text == "Biztonságos részlet token=[REDACTED]", "A jelölt eszközhiba szűrt szövege elveszett.");
        var call = Assert.Single(store.Calls);
        Assert.Equal("Failed", call.State);
        Assert.True(call.SanitizedError == text, "A válasz és az audit hibaszövege eltér.");
        Assert.True(!JsonSerializer.Serialize(call).Contains(secret, StringComparison.Ordinal), "Az audit titkot tartalmaz.");
        Assert.True(!response.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "A válasz titkot tartalmaz.");
        Assert.True(!probe.Logs.Contains(secret), "A napló titkot tartalmaz.");
    }

    [Fact]
    public async Task MissingSwitchStartsRealHostReadOnlyWithoutIntegrationCredentials()
    {
        var probe = new ProbeTools();
        var store = new RecordingStore();
        await using var factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton<IAuditStore>(store);
            services.AddMcpServer().WithTools(probe);
        })
        { InitializeKillSwitch = false };
        Assert.IsType<AgentGate.Secrets.BitwardenSecretProvider>(factory.Services.GetRequiredService<AgentGate.Secrets.ISecretProvider>());
        Assert.Null(factory.Services.GetService<AgentGate.Integrations.HomeAssistant.HomeAssistantClient>());
        using var read = await CallAsync(factory, "count", new { });
        Assert.False(IsError(read));
        using var write = await CallAsync(factory, "failure", new { });
        Assert.True(IsError(write));
        Assert.Equal(1, probe.Count);
        Assert.Equal("Failed", store.Calls[1].State);
        Assert.Equal("A vészleállító policy letiltotta az eszköz végrehajtását.", store.Calls[1].SanitizedError);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("false")]
    [InlineData("{\"Enabled\":null}")]
    [InlineData("{\"Enabled\":0}")]
    [InlineData("{\"Enabled\":\"false\"}")]
    [InlineData("{\"enabled\":false}")]
    [InlineData("{\"Enabled\":false,}")]
    [InlineData("{\"Enabled\":false}x")]
    [InlineData("/* megjegyzés */{\"Enabled\":false}")]
    [InlineData("{")]
    [InlineData("{\"Enabled\":false,\"Enabled\":false}")]
    [InlineData("{\"Enabled\":true,\"Enabled\":false}")]
    [InlineData("{\"Enabled\":false,\"Other\":true}")]
    public async Task InvalidSwitchCannotRetainPreviousPermissiveState(string invalid)
    {
        var probe = new ProbeTools();
        var store = new RecordingStore();
        await using var factory = CreateFactory(probe, store);
        using var initial = await CallAsync(factory, "write", new { });
        Assert.False(IsError(initial));
        await File.WriteAllTextAsync(factory.KillSwitchPath, invalid);
        using var rejected = await CallAsync(factory, "write", new { });
        Assert.True(IsError(rejected));
        Assert.Equal(1, probe.Count);
        Assert.Equal("Failed", store.Calls[1].State);
    }

    [Theory]
    [InlineData(LogLevel.Information)]
    [InlineData(LogLevel.Trace)]
    public async Task UnknownToolNameNeverLeaksThroughResponseOrNormalLogging(LogLevel minimumLevel)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools();
        var store = new RecordingStore();
        await using var factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(minimumLevel).AddProvider(probe.Logs));
            services.AddSingleton<IAuditStore>(store);
            services.AddMcpServer().WithTools(probe);
        });
        using var response = await CallAsync(factory, secret, new Dictionary<string, string> { [secret] = secret });
        Assert.True(IsError(response));
        Assert.Equal(0, probe.Count);
        Assert.True(!response.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "A hiba titkot tartalmaz.");
        Assert.True(!probe.Logs.Contains(secret), "A normál SDK-napló titkot tartalmaz.");
        factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentGate.LoggingProbe")
            .LogInformation("Biztonságos alkalmazásnapló-próba.");
        Assert.True(probe.Logs.Contains("Biztonságos alkalmazásnapló-próba."), "Az alkalmazásnaplózás nem némítható el.");
    }

    [Theory]
    [InlineData("missing", RiskLevel.Read)]
    [InlineData("missing-risk", RiskLevel.Forbidden)]
    [InlineData("invalid-risk", RiskLevel.Forbidden)]
    [InlineData(null, RiskLevel.Forbidden)]
    public async Task RejectedCallsAreDurableWithoutRawNamesOrArguments(string? toolName, RiskLevel expectedRisk)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools();
        await using var factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(probe.Logs));
            services.AddMcpServer().WithTools(probe);
        });
        using var response = await CallAsync(factory, toolName ?? secret, new Dictionary<string, string>
        {
            [secret] = secret,
            ["value"] = secret
        });
        Assert.True(IsError(response));
        Assert.Equal(0, probe.Count);
        Assert.True(!response.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "A hiba titkot tartalmaz.");
        Assert.True(!probe.Logs.Contains(secret), "A normál napló titkot tartalmaz.");
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT Tool, RiskLevel, SanitizedParameters, State, DurationMilliseconds, SanitizedError FROM AuditCalls";
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetString(0) == (toolName ?? "[ismeretlen-eszköz]"), "Az audit eszközneve nem biztonságos.");
        Assert.Equal((int)expectedRisk, reader.GetInt32(1));
        Assert.True(reader.GetString(2) == "{}", "Az elutasított hívás argumentumai nem tárolhatók.");
        Assert.Equal("Failed", reader.GetString(3));
        Assert.True(reader.GetDouble(4) >= 0);
        Assert.True(reader.GetString(5).Contains("letiltva", StringComparison.Ordinal), "Hiányzó elutasítási indok.");
        for (var index = 0; index < reader.FieldCount; index++)
        {
            Assert.True(!reader.GetValue(index).ToString()!.Contains(secret, StringComparison.Ordinal), "A tartós audit titkot tartalmaz.");
        }
        Assert.False(await reader.ReadAsync());
    }

    [Theory]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task RejectedCallsRetainAuditFailureSemantics(string? toolName, bool completionFailure)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools();
        var store = new RecordingStore
        {
            BeginError = completionFailure ? null : secret,
            CompleteError = completionFailure ? secret : null
        };
        await using var factory = CreateFactory(probe, store);
        using var response = await CallAsync(factory, toolName ?? secret, new { });
        Assert.True(IsError(response));
        Assert.Equal(0, probe.Count);
        Assert.True(!response.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "A hiba titkot tartalmaz.");
        Assert.True(!probe.Logs.Contains(secret), "A normál napló titkot tartalmaz.");
        Assert.True(store.Calls.Count == (completionFailure ? 1 : 0), "Hibás auditkezdési állapot.");
        if (completionFailure)
        {
            Assert.Equal("Started", store.Calls[0].State);
            Assert.Null(store.Calls[0].DurationMilliseconds);
        }
        var message = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.True(message.Contains(completionFailure ? "lezárás" : "előzetes", StringComparison.Ordinal), "Hiányzó audithiba.");
    }

    [Fact]
    public async Task TraceLoggingCannotExposeSuccessfulPayloadOrArgumentsEvenWithProviderSpecificOverrides()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools { Payload = secret };
        var store = new RecordingStore();
        await using var factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(probe.Logs)
                .AddFilter<RecordingLogger>("ModelContextProtocol.Server.McpServer", LogLevel.Trace)
                .AddFilter<RecordingLogger>("ModelContextProtocol.Server.StreamableHttpServerTransport", LogLevel.Trace));
            services.AddSingleton<IAuditStore>(store);
            services.AddMcpServer().WithTools(probe);
        });
        using var response = await CallAsync(factory, "count", new Dictionary<string, string> { [secret] = secret });
        Assert.False(IsError(response));
        Assert.Equal(1, probe.Count);
        // The successful payload is intentionally returned, but must never become a log entry.
        Assert.True(response.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "A sikeres toolválasz megváltozott.");
        Assert.True(!probe.Logs.Contains(secret), "A normál napló titkot tartalmaz.");
        Assert.True(!JsonSerializer.Serialize(store.Calls).Contains(secret, StringComparison.Ordinal), "Az audit titkot tartalmaz.");
        factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentGate.LoggingProbe").LogTrace("Biztonságos trace-próba.");
        factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.LoggingProbe").LogInformation("Biztonságos HTTP-próba.");
        Assert.True(probe.Logs.Contains("Biztonságos trace-próba."), "Az alkalmazásnaplózás nem némítható el.");
        Assert.True(probe.Logs.Contains("Biztonságos HTTP-próba."), "Az ASP.NET-naplózás nem némítható el.");
    }

    [Fact]
    public async Task SuccessAuditsOnlyAllowedScalarValuesAndNeverRawNamesOrPayload()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools { Payload = secret };
        var store = new RecordingStore();
        await using var factory = CreateFactory(probe, store);
        var before = DateTimeOffset.UtcNow;
        using var response = await CallAsync(factory, "accept", new Dictionary<string, object>
        {
            ["description"] = "ártalmatlan token=" + secret,
            ["password"] = secret,
            ["nested"] = new Dictionary<string, string> { [secret] = secret },
            [secret] = secret,
            ["Authorization"] = "Bearer " + secret
        });
        Assert.False(IsError(response));
        Assert.Equal(1, probe.Count);
        var call = Assert.Single(store.Calls);
        Assert.Equal("test-caller", call.Caller);
        Assert.Equal("accept", call.Tool);
        Assert.Equal(RiskLevel.Control, call.RiskLevel);
        Assert.NotEqual(Guid.Empty, call.CallId);
        Assert.Equal(TimeSpan.Zero, call.TimestampUtc.Offset);
        Assert.InRange(call.TimestampUtc, before, DateTimeOffset.UtcNow);
        Assert.Equal("Succeeded", call.State);
        Assert.True(call.DurationMilliseconds >= 0);
        Assert.Null(call.SanitizedError);
        Assert.True(!JsonSerializer.Serialize(call).Contains(secret, StringComparison.Ordinal), "Az audit titkot tartalmaz.");
        Assert.True(!probe.Logs.Contains(secret), "A szervernapló titkot tartalmaz.");
        using var parameters = JsonDocument.Parse(call.SanitizedParameters);
        Assert.Equal("ártalmatlan token=[REDACTED]", parameters.RootElement.GetProperty("description").GetString());
        Assert.Equal("[REDACTED]", parameters.RootElement.GetProperty("password").GetString());
        Assert.Equal("[kihagyva]", parameters.RootElement.GetProperty("nested").GetString());
        Assert.Equal("[kihagyva]", parameters.RootElement.GetProperty("[kihagyva]").GetString());
        Assert.False(parameters.RootElement.TryGetProperty("Authorization", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolFailureNeverCopiesRawExceptionOrErrorPayload(bool returnError)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools { Payload = secret, ReturnError = returnError };
        var store = new RecordingStore();
        await using var factory = CreateFactory(probe, store);
        using var response = await CallAsync(factory, "failure", new { });
        Assert.True(IsError(response));
        Assert.Equal(1, probe.Count);
        var call = Assert.Single(store.Calls);
        Assert.Equal("Failed", call.State);
        Assert.True(call.DurationMilliseconds >= 0);
        Assert.False(string.IsNullOrWhiteSpace(call.SanitizedError));
        Assert.True(!JsonSerializer.Serialize(call).Contains(secret, StringComparison.Ordinal), "Az audit titkot tartalmaz.");
        Assert.True(!response.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "A hiba titkot tartalmaz.");
        Assert.True(!probe.Logs.Contains(secret), "A szervernapló titkot tartalmaz.");
    }

    [Fact]
    public async Task BeginFailureStopsExecutionAndReturnsSafeError()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools();
        var store = new RecordingStore { BeginError = secret };
        await using var factory = CreateFactory(probe, store);
        using var response = await CallAsync(factory, "count", new { });
        Assert.True(IsError(response));
        Assert.Equal(0, probe.Count);
        Assert.Empty(store.Calls);
        Assert.True(!response.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "A hiba titkot tartalmaz.");
        Assert.True(!probe.Logs.Contains(secret), "A szervernapló titkot tartalmaz.");
        Assert.Contains("előzetes", response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task CompleteFailureReportsIncompleteAuditWithoutClaimingRollback()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools();
        var store = new RecordingStore { CompleteError = secret };
        await using var factory = CreateFactory(probe, store);
        using var response = await CallAsync(factory, "count", new { });
        Assert.True(IsError(response));
        Assert.Equal(1, probe.Count);
        Assert.Equal("Started", Assert.Single(store.Calls).State);
        Assert.True(!response.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "A hiba titkot tartalmaz.");
        Assert.True(!probe.Logs.Contains(secret), "A szervernapló titkot tartalmaz.");
        var message = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("lezárás", message);
        Assert.Contains("nem jelenti", message);
    }

    [Fact]
    public async Task ClientCancellationIsForwardedAndFinalizedWithIndependentBoundedToken()
    {
        var probe = new ProbeTools();
        var store = new RecordingStore();
        await using var factory = CreateFactory(probe, store);
        using var cancellation = new CancellationTokenSource();
        var pending = CallAsync(factory, "wait", new { }, cancellation.Token);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, probe.Count);
        Assert.True(probe.ObservedCancellation);
        Assert.Equal("Cancelled", Assert.Single(store.Calls).State);
        Assert.True(Assert.Single(store.Calls).DurationMilliseconds >= 0);
        Assert.True(store.CompletionTokenCanBeCancelled);
    }

    [Fact]
    public async Task ConfiguredCallerIsFilteredAndCannotBeOverriddenByArguments()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var probe = new ProbeTools();
        var store = new RecordingStore();
        await using var factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton<IAuditStore>(store);
            services.AddMcpServer().WithTools(probe);
            services.PostConfigure<AuditOptions>(options => options.Caller = "caller token=" + secret);
        });
        using var response = await CallAsync(factory, "count", new { caller = "self-reported" });
        Assert.False(IsError(response));
        var call = Assert.Single(store.Calls);
        Assert.True(!call.Caller.Contains(secret, StringComparison.Ordinal), "A hívó titkot tartalmaz.");
        Assert.Equal("caller token=[REDACTED]", call.Caller);
        Assert.DoesNotContain("self-reported", call.SanitizedParameters);
    }

    [Fact]
    public async Task DurationIncludesAwaitedToolExecution()
    {
        var store = new RecordingStore();
        var probe = new ProbeTools();
        await using var factory = CreateFactory(probe, store);
        using var response = await CallAsync(factory, "delay", new { });
        Assert.False(IsError(response));
        Assert.Equal(1, probe.Count);
        Assert.True(Assert.Single(store.Calls).DurationMilliseconds >= 35);
    }

    [Fact]
    public async Task RealSqlitePrewriteFailurePreventsToolExecution()
    {
        var probe = new ProbeTools();
        await using var factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            services => services.AddMcpServer().WithTools(probe));
        await File.WriteAllTextAsync(factory.AuditPath, "nem SQLite adatbázis");
        using var response = await CallAsync(factory, "count", new { });
        Assert.True(IsError(response));
        Assert.Equal(0, probe.Count);
    }

    [Fact]
    public async Task RealSqliteCompletionFailurePreservesDurableStartedCall()
    {
        var probe = new ProbeTools();
        await using var factory = new McpTestApplication(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            services => services.AddMcpServer().WithTools(probe));
        var seed = new AuditCall(Guid.NewGuid(), DateTimeOffset.UtcNow, "seed", "seed", RiskLevel.Read, "{}");
        await new SqliteAuditStore(factory.AuditPath).BeginAsync(seed, CancellationToken.None);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={factory.AuditPath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailComplete BEFORE UPDATE ON AuditCalls BEGIN SELECT RAISE(ABORT, 'tesztlezárási hiba'); END;";
            await command.ExecuteNonQueryAsync();
        }
        using var response = await CallAsync(factory, "count", new { });
        Assert.True(IsError(response));
        Assert.Equal(1, probe.Count);
        await using var reopened = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={factory.AuditPath};Pooling=False");
        await reopened.OpenAsync();
        await using var read = reopened.CreateCommand();
        read.CommandText = "SELECT State, DurationMilliseconds FROM AuditCalls WHERE Tool = 'count'";
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Started", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.False(await reader.ReadAsync());
    }

    [Theory]
    [InlineData("\"ártalmatlan\"", "ártalmatlan")]
    [InlineData("42", "42")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("null", null)]
    [InlineData("{}", "[kihagyva]")]
    [InlineData("[]", "[kihagyva]")]
    public async Task AllowedScalarsArePreservedButContainersAreOmitted(string json, string? expected)
    {
        var probe = new ProbeTools();
        var store = new RecordingStore();
        await using var factory = CreateFactory(probe, store);
        using var value = JsonDocument.Parse(json);
        using var response = await CallAsync(factory, "scalar", new { value = value.RootElement });
        Assert.False(IsError(response));
        using var parameters = JsonDocument.Parse(Assert.Single(store.Calls).SanitizedParameters);
        Assert.Equal(expected, parameters.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task OversizedAllowedNumericValueAlsoPassesThroughRedactor()
    {
        var probe = new ProbeTools();
        var store = new RecordingStore();
        await using var factory = CreateFactory(probe, store);
        using var value = JsonDocument.Parse("1" + new string('7', 65_536));
        using var response = await CallAsync(factory, "scalar", new { value = value.RootElement });
        Assert.False(IsError(response));
        using var parameters = JsonDocument.Parse(Assert.Single(store.Calls).SanitizedParameters);
        Assert.True(parameters.RootElement.GetProperty("value").GetString() == "[REDACTED]", "A túlméretes érték nincs kitakarva.");
    }

    private static McpTestApplication CreateFactory(ProbeTools probe, IAuditStore store) => new(
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders().AddProvider(probe.Logs));
            services.AddSingleton(store);
            services.AddMcpServer().WithTools(probe);
        });

    private static async Task<JsonDocument> CallAsync(McpTestApplication factory, string tool, object arguments,
        CancellationToken cancellationToken = default)
    {
        using var client = factory.CreateClient();
        var token = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpAuthenticationOptions>>().Value.Token!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
        using var response = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments }
        }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            body = body.Split('\n').Last(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        }
        return JsonDocument.Parse(body);
    }

    private static bool IsError(JsonDocument document) => document.RootElement.TryGetProperty("error", out _) ||
        (document.RootElement.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean());

    public sealed class ProbeTools
    {
        public int Count { get; private set; }
        public RecordingLogger Logs { get; } = new();
        public string Payload { get; init; } = "ok";
        public bool ReturnError { get; init; }
        public int FailureKind { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedCancellation { get; private set; }

        [McpServerTool(Name = "wait"), ToolRisk(RiskLevel.Read), ToolAuditParameters]
        public async Task<string> Wait(CancellationToken cancellationToken)
        {
            Count++;
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ObservedCancellation = cancellationToken.IsCancellationRequested;
                throw;
            }
            return "ok";
        }

        [McpServerTool(Name = "scalar"), ToolRisk(RiskLevel.Read), ToolAuditParameters("value")]
        public string Scalar(JsonElement value)
        {
            Count++;
            return "ok";
        }

        [McpServerTool(Name = "delay"), ToolRisk(RiskLevel.Read), ToolAuditParameters]
        public async Task<string> Delay(CancellationToken cancellationToken)
        {
            Count++;
            await Task.Delay(50, cancellationToken);
            return "ok";
        }

        [McpServerTool(Name = "count"), ToolRisk(RiskLevel.Read), ToolAuditParameters]
        public string Execute()
        {
            Count++;
            return Payload;
        }

        [McpServerTool(Name = "unmarked"), ToolRisk(RiskLevel.Read), ToolAuditParameters]
        public CallToolResult Unmarked()
        {
            Count++;
            if (FailureKind == 0)
            {
                throw new ModelContextProtocol.McpException(Payload);
            }
            if (FailureKind == 1)
            {
                var error = new InvalidOperationException(Payload, new Exception(Payload));
                error.Data[Payload] = Payload;
                throw error;
            }
            if (FailureKind == 2)
            {
                throw new InvalidOperationException(Payload, new AgentGate.Tools.GateToolException(Payload));
            }
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = Payload }],
                StructuredContent = JsonSerializer.SerializeToElement(new { detail = Payload })
            };
        }

        [McpServerTool(Name = "sensitive"), ToolRisk(RiskLevel.SensitiveControl), ToolAuditParameters]
        public string Sensitive()
        {
            Count++;
            return "ok";
        }

        [McpServerTool(Name = "configure"), ToolRisk(RiskLevel.Configuration), ToolAuditParameters]
        public string Configure()
        {
            Count++;
            return "ok";
        }

        [McpServerTool(Name = "system"), ToolRisk(RiskLevel.System), ToolAuditParameters]
        public string SystemOperation()
        {
            Count++;
            return "ok";
        }

        [McpServerTool(Name = "forbidden"), ToolRisk(RiskLevel.Forbidden), ToolAuditParameters]
        public string Forbidden()
        {
            Count++;
            return "ok";
        }

        [McpServerTool(Name = "safe-failure"), ToolRisk(RiskLevel.Read), ToolAuditParameters]
        public string SafeFailure()
        {
            Count++;
            throw new AgentGate.Tools.GateToolException(Payload);
        }

        [McpServerTool(Name = "write"), ToolRisk(RiskLevel.Control), ToolAuditParameters]
        public string Write()
        {
            Count++;
            return "ok";
        }

        [McpServerTool(Name = "failure"), ToolRisk(RiskLevel.Control), ToolAuditParameters]
        public CallToolResult Failure()
        {
            Count++;
            if (!ReturnError)
            {
                throw new ModelContextProtocol.McpException(Payload);
            }
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = Payload }] };
        }

        [McpServerTool(Name = "accept"), ToolRisk(RiskLevel.Control), ToolAuditParameters("description", "password", "nested")]
        public string Accept(string description, string password, JsonElement nested)
        {
            Count++;
            return Payload;
        }

        [McpServerTool(Name = "missing-risk"), ToolAuditParameters]
        public string MissingRisk()
        {
            Count++;
            return "ok";
        }

        [McpServerTool(Name = "invalid-risk"), ToolRisk((RiskLevel)999), ToolAuditParameters]
        public string InvalidRisk()
        {
            Count++;
            return "ok";
        }

        [McpServerTool(Name = "missing"), ToolRisk(RiskLevel.Read)]
        public string Missing()
        {
            Count++;
            return "ok";
        }
    }

    public sealed class RecordingLogger : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> messages = new();
        public bool Contains(string value) => messages.Any(message => message.Contains(value, StringComparison.Ordinal));
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            CaptureState(state);
            return null;
        }
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Enqueue(formatter(state, exception) + exception?.ToString());
            CaptureState(state);
        }
        private void CaptureState<TState>(TState state)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                foreach (var property in properties)
                {
                    messages.Enqueue(property.Key + "=" + property.Value);
                }
            }
            else
            {
                messages.Enqueue(state?.ToString() ?? "");
            }
        }
        public void Dispose() { }
    }

    private sealed class RecordingStore : IAuditStore
    {
        public Task CompletePendingAsync(Guid callId, string state, string? sanitizedError, CancellationToken cancellationToken) =>
            CompleteAsync(callId, state, 0, sanitizedError, cancellationToken);
        public List<AuditCall> Calls { get; } = [];
        public string? BeginError { get; init; }
        public string? CompleteError { get; init; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CompletionTokenCanBeCancelled { get; private set; }
        public Task BeginAsync(AuditCall call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeginError is not null)
            {
                throw new ModelContextProtocol.McpException(BeginError);
            }
            Calls.Add(call);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(Guid callId, string state, double durationMilliseconds, string? sanitizedError, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CompleteError is not null)
            {
                throw new ModelContextProtocol.McpException(CompleteError);
            }
            var index = Calls.FindIndex(call => call.CallId == callId);
            Calls[index] = Calls[index] with { State = state, DurationMilliseconds = durationMilliseconds, SanitizedError = sanitizedError };
            CompletionTokenCanBeCancelled = cancellationToken.CanBeCanceled;
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
