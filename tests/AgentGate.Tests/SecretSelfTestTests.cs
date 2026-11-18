using System.Collections.Concurrent;
using System.Security.Cryptography;
using AgentGate.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentGate.Tests;

public sealed class SecretSelfTestTests
{
    [Fact]
    public async Task SelfTestDoesNotMistakeWarmCacheForFreshBitwardenAccess()
    {
        var id = Guid.NewGuid();
        var hidden = NewSecret();
        var raw = new FakeProvider((_, _) => Task.FromResult(hidden));
        await using var cache = new CachedSecretProvider(raw, TimeProvider.System);
        var warm = await cache.GetSecretLeaseAsync(id);
        var log = new CaptureLogger();
        await using var services = new ServiceCollection().AddSingleton<ISecretProvider>(cache).BuildServiceProvider();
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(log));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:SelfTestSecretId"] = id.ToString()
        }).Build();
        using var service = new SecretSelfTestService(configuration, services, logging.CreateLogger<SecretSelfTestService>());
        await service.StartAsync(default);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, raw.Calls);
        Assert.Same(warm, await cache.GetSecretLeaseAsync(id));
        AssertSafe(log, id.ToString(), hidden);
        Assert.True(log.Entries.Single().Message == "A Bitwarden önteszt sikeres.", "Az önteszt nem friss sikeres olvasást jelzett.");
    }

    [Fact]
    public async Task ProgramReadsConfiguredSecretExactlyOnceAndLogsOnlyFixedSuccess()
    {
        var id = Guid.NewGuid();
        var value = NewSecret();
        var log = new CaptureLogger();
        var provider = new FakeProvider((actual, _) =>
        {
            Assert.True(actual == id, "Eltérő titokazonosító.");
            return Task.FromResult(value);
        });
        await using var app = CreateApp(id.ToString(), provider, log);
        using var client = app.CreateClient();
        var completed = await Record.ExceptionAsync(() => log.Written.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(completed is null, "Az önteszt nem naplózott eredményt.");
        Assert.Equal(1, provider.Calls);
        AssertSafe(log, id.ToString(), value);
        Assert.True(log.Entries.Single().Message == "A Bitwarden önteszt sikeres.", "Eltérő sikerüzenet.");
        Assert.Equal("ok", await client.GetStringAsync("/health"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidConfigurationLogsFixedFailureWithoutResolvingProvider(string configured)
    {
        configured = configured == "invalid" ? NewSecret() : configured;
        var log = new CaptureLogger();
        var provider = new FakeProvider((_, _) => throw new InvalidOperationException(NewSecret()));
        using var services = new ServiceCollection().AddSingleton<ISecretProvider>(provider).BuildServiceProvider();
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(log));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:SelfTestSecretId"] = configured
        }).Build();
        using var service = new SecretSelfTestService(configuration, services, logging.CreateLogger<SecretSelfTestService>());
        await service.StartAsync(default);
        var completed = await Record.ExceptionAsync(() => service.ExecuteTask!);
        Assert.True(completed is null, "A konfigurációhiba kivétellel zárult.");
        Assert.True(log.Entries.Single().Message == "A Bitwarden önteszt sikertelen. Hibatípus: Configuration.", "Eltérő konfigurációhiba.");
        Assert.Equal(0, provider.Calls);
        AssertSafe(log, configured.Length > 5 ? configured : NewSecret());
    }

    [Theory]
    [InlineData(-1, "ProviderFailure")]
    [InlineData(0, "MissingAccessToken")]
    [InlineData(1, "NotFound")]
    [InlineData(2, "AuthenticationFailed")]
    [InlineData(3, "ProviderFailure")]
    [InlineData(4, "UnsafeDiagnostics")]
    [InlineData(999, "ProviderFailure")]
    public async Task ProviderFailureIsObservedAndOnlyAllowlistedErrorIsLogged(int code, string expected)
    {
        var id = Guid.NewGuid();
        var token = NewSecret();
        var value = NewSecret();
        Exception failure = code == -1 ? new InvalidOperationException(token + value) : new SecretProviderException((SecretProviderError)code);
        failure.Source = token;
        failure.Data[id] = value;
        var log = new CaptureLogger();
        var provider = new FakeProvider((_, _) => Task.FromException<string>(failure));
        using var services = new ServiceCollection().AddSingleton<ISecretProvider>(provider).BuildServiceProvider();
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(log));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:SelfTestSecretId"] = id.ToString()
        }).Build();
        using var service = new SecretSelfTestService(configuration, services, logging.CreateLogger<SecretSelfTestService>());
        await service.StartAsync(default);
        var error = await Record.ExceptionAsync(() => service.ExecuteTask!);
        Assert.True(error is null, "A szolgáltatói hiba kijutott a háttérszolgáltatásból.");
        Assert.True(log.Entries.Single().Message == $"A Bitwarden önteszt sikertelen. Hibatípus: {expected}.", "Eltérő hibatípus.");
        Assert.Equal(1, provider.Calls);
        AssertSafe(log, id.ToString(), token, value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task TimeoutCancelsPromptlyButOwnsUncooperativeWorkUntilCleanup(bool synchronous, bool lateSuccess)
    {
        var id = Guid.NewGuid();
        var token = NewSecret();
        var value = NewSecret();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new CaptureLogger();
        var provider = new FakeProvider((_, cancellation) =>
        {
            cancellation.Register(() =>
            {
                canceled.TrySetResult();
                if (lateSuccess)
                {
                    throw new InvalidOperationException(token);
                }
            });
            entered.TrySetResult();
            if (synchronous)
            {
                released.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException(token + value);
            }
            return FinishAsync();
            async Task<string> FinishAsync()
            {
                await released.Task;
                if (lateSuccess)
                {
                    return value;
                }
                throw new InvalidOperationException(token + value);
            }
        });
        using var services = new ServiceCollection().AddSingleton<ISecretProvider>(provider).BuildServiceProvider();
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(log));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:SelfTestSecretId"] = id.ToString()
        }).Build();
        using var service = new SecretSelfTestService(configuration, services, logging.CreateLogger<SecretSelfTestService>());
        try
        {
            await service.StartAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var timedOut = await Record.ExceptionAsync(() => log.Written.Task.WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.True(timedOut is null, "Az önteszt nem jelzett időben időtúllépést.");
            Assert.True(log.Entries.Single().Message == "A Bitwarden önteszt sikertelen. Hibatípus: Timeout.", "Eltérő időtúllépési napló.");
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(service.ExecuteTask!.IsCompleted);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await service.StopAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(service.ExecuteTask.IsCompleted);
        }
        finally
        {
            released.TrySetResult();
            var cleanup = await Record.ExceptionAsync(() => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(cleanup is null, "A késői hiba nem lett megfigyelve.");
        }
        Assert.Equal(1, provider.Calls);
        Assert.True(log.Entries.Count == 1, "Ismételt öntesztnapló keletkezett.");
        AssertSafe(log, id.ToString(), token, value);
    }

    [Fact]
    public async Task MissingSettingDoesNotResolveProviderOrLogAnything()
    {
        var log = new CaptureLogger();
        var resolutions = 0;
        using var services = new ServiceCollection().AddSingleton<ISecretProvider>(_ =>
        {
            Interlocked.Increment(ref resolutions);
            throw new InvalidOperationException(NewSecret());
        }).BuildServiceProvider();
        using var logging = LoggerFactory.Create(builder => builder.AddProvider(log));
        using var service = new SecretSelfTestService(new ConfigurationBuilder().Build(), services, logging.CreateLogger<SecretSelfTestService>());
        await service.StartAsync(default);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, resolutions);
        Assert.True(log.Entries.IsEmpty, "Váratlan öntesztnapló keletkezett.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgramRemainsHealthyAfterSelfTestFailure(bool invalidConfiguration)
    {
        var id = Guid.NewGuid();
        var token = NewSecret();
        var value = NewSecret();
        var log = new CaptureLogger();
        var provider = new FakeProvider((_, _) => throw new InvalidOperationException(token + value));
        await using var app = CreateApp(invalidConfiguration ? token : id.ToString(), provider, log);
        using var client = app.CreateClient();
        await log.Written.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var service = app.Services.GetServices<IHostedService>().OfType<SecretSelfTestService>().Single();
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ok", await client.GetStringAsync("/health"));
        Assert.Equal(invalidConfiguration ? 0 : 1, provider.Calls);
        AssertSafe(log, id.ToString(), token, value);
    }

    [Fact]
    public async Task BlockingProviderDoesNotBlockProgramStartupOrHealthAndShutdownIsBounded()
    {
        var id = Guid.NewGuid();
        var value = NewSecret();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new CaptureLogger();
        var provider = new FakeProvider((_, cancellation) =>
        {
            cancellation.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            released.Task.GetAwaiter().GetResult();
            return Task.FromResult(value);
        });
        await using var app = CreateApp(id.ToString(), provider, log);
        SecretSelfTestService? service = null;
        try
        {
            using var client = await Task.Run(() => app.CreateClient()).WaitAsync(TimeSpan.FromSeconds(3));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("ok", await client.GetStringAsync("/health").WaitAsync(TimeSpan.FromSeconds(2)));
            service = app.Services.GetServices<IHostedService>().OfType<SecretSelfTestService>().Single();
            var host = app.Services.GetRequiredService<IHost>();
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await host.StopAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(2));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally
        {
            released.TrySetResult();
            if (service is not null)
            {
                await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        Assert.True(log.Entries.IsEmpty, "Váratlan öntesztnapló keletkezett.");
        AssertSafe(log, id.ToString(), value);
    }

    private static WebApplicationFactory<Program> CreateApp(string? id, FakeProvider provider, CaptureLogger log) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Secrets:SelfTestSecretId"] = id }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISecretProvider>();
                services.AddSingleton<ISecretProvider>(provider);
                services.AddSingleton<ILoggerProvider>(log);
            });
        });

    private static void AssertSafe(CaptureLogger log, params string[] sensitive)
    {
        foreach (var entry in log.Entries)
        {
            Assert.True(entry.Exception is null, "Kivételobjektum került a naplóba.");
            Assert.True(entry.Scopes.Length == 0, "Az önteszt naplóscope-ot nyitott.");
            Assert.True(entry.State.All(field => field.Key is "{OriginalFormat}" or "ErrorType"), "Nem engedélyezett naplómező.");
            Assert.True(entry.State.Where(field => field.Key == "ErrorType").All(field => field.Value is
                "MissingAccessToken" or "NotFound" or "AuthenticationFailed" or "ProviderFailure" or "UnsafeDiagnostics"),
                "Nem engedélyezett strukturált hibatípus.");
        }
        foreach (var entry in log.AllEntries)
        {
            foreach (var text in sensitive)
            {
                Assert.True(!entry.Message.Contains(text, StringComparison.Ordinal) &&
                    !entry.Category.Contains(text, StringComparison.Ordinal) &&
                    !(entry.EventId.Name?.Contains(text, StringComparison.Ordinal) ?? false) &&
                    !(entry.Exception?.ToString().Contains(text, StringComparison.Ordinal) ?? false) &&
                    !entry.Scopes.Any(scope => scope?.ToString()?.Contains(text, StringComparison.Ordinal) ?? false) &&
                    !entry.State.Any(field => field.Key.Contains(text, StringComparison.Ordinal) ||
                        (field.Value?.ToString()?.Contains(text, StringComparison.Ordinal) ?? false)), "Bizalmas adat került a naplóba.");
            }
        }
    }

    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(48));

    private sealed class FakeProvider(Func<Guid, CancellationToken, Task<string>> read) : ISecretProvider
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public Task<string> GetSecretAsync(Guid secretId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return read(secretId, cancellationToken);
        }
    }

    private sealed record Entry(string Category, EventId EventId, string Message, Exception? Exception, KeyValuePair<string, object?>[] State, object?[] Scopes);

    private sealed class CaptureLogger : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public ConcurrentQueue<Entry> AllEntries { get; } = new();
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }

        private sealed class Logger(CaptureLogger owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var active = new List<object?>();
                owner.scopes.ForEachScope((scope, list) => list.Add(scope), active);
                var entry = new Entry(category, eventId, formatter(state, exception), exception,
                    (state as IEnumerable<KeyValuePair<string, object?>>)?.ToArray() ?? [], active.ToArray());
                owner.AllEntries.Enqueue(entry);
                if (category.Contains("SecretSelfTest", StringComparison.Ordinal))
                {
                    owner.Entries.Enqueue(entry);
                    owner.Written.TrySetResult();
                }
            }
        }
    }
}
