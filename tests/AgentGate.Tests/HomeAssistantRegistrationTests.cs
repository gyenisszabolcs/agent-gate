using System.Collections.Concurrent;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    public async Task ExtensionRejectsInvalidSecretReferenceAtHostStart(string? id)
    {
        using var host = CreateHost(id);
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal("A Home Assistant konfiguráció érvénytelen.", Assert.Single(error.Failures));
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task ExtensionAcceptsGuidWithoutLookingUpSecretOrSendingHttp()
    {
        using var host = CreateHost(Guid.NewGuid().ToString());
        await host.StartAsync();
        Assert.NotNull(host.Services.GetRequiredService<HomeAssistantClient>());
        await host.StopAsync();
    }

    [Theory]
    [InlineData("BaseUrl", "http://ha.example/")]
    [InlineData("BaseUrl", "https://user:password@ha.example/")]
    [InlineData("BaseUrl", "https://ha.example/prefix/")]
    [InlineData("BaseUrl", "https://ha.example/?token=hidden")]
    [InlineData("BaseUrl", "https://ha.example/#fragment")]
    [InlineData("BaseUrl", "file:///data")]
    [InlineData("BaseUrl", "")]
    [InlineData("Timeout", "00:00:00")]
    [InlineData("Timeout", "-00:00:01")]
    [InlineData("Timeout", "01:00:00")]
    [InlineData("DomainAllowlist:0", "*")]
    [InlineData("DomainAllowlist:0", "Sensor")]
    public async Task UnsafeOptionsFailWithFixedMessage(string key, string value)
    {
        using var host = CreateHost(Guid.NewGuid().ToString(), key, value);
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal("A Home Assistant konfiguráció érvénytelen.", Assert.Single(error.Failures));
    }

    [Theory]
    [InlineData("http://10.0.0.0/")]
    [InlineData("http://10.255.255.255/")]
    [InlineData("http://172.16.0.0/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.0.0/")]
    [InlineData("http://192.168.255.255/")]
    public async Task ExplicitHttpAllowsOnlyPrivateIpv4Ranges(string baseUrl)
    {
        using var host = CreateHost(Guid.NewGuid().ToString(), "BaseUrl", baseUrl, "true");
        await host.StartAsync();
        Assert.NotNull(host.Services.GetRequiredService<HomeAssistantClient>());
        await host.StopAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    public async Task MalformedHttpFlagFailsClosedEvenForHttps(string setting)
    {
        using var host = CreateHost(Guid.NewGuid().ToString(), allowHttp: setting);
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal(HomeAssistantOptionsValidator.SafeMessage, Assert.Single(error.Failures));
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("http://192.168.1.2:8123/", "true", 1)]
    [InlineData("https://ha.example/", "true", 1)]
    [InlineData("https://ha.example/", "false", 0)]
    [InlineData("https://ha.example/", null, 0)]
    public async Task HostWarnsOnlyWhenHttpExceptionIsEnabled(string baseUrl, string? allowHttp, int warnings)
    {
        var logs = new CapturingLogs();
        using var host = CreateHost(Guid.NewGuid().ToString(), "BaseUrl", baseUrl, allowHttp, logs);
        await host.StartAsync();
        var entries = logs.Entries.Where(entry => entry.Level == LogLevel.Warning).ToArray();
        Assert.Equal(warnings, entries.Length);
        foreach (var entry in entries)
        {
            Assert.Equal("A Home Assistant HTTP-kivétel engedélyezett; a titkosítatlan kapcsolat Bearer tokent tehet hozzáférhetővé.", entry.Message);
            Assert.Null(entry.Exception);
        }
        await host.StopAsync();
    }

    [Theory]
    [InlineData("http://9.255.255.255/")]
    [InlineData("http://11.0.0.0/")]
    [InlineData("http://172.15.255.255/")]
    [InlineData("http://172.32.0.0/")]
    [InlineData("http://192.167.255.255/")]
    [InlineData("http://192.169.0.0/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.1.2/")]
    [InlineData("http://100.64.0.0/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://255.255.255.255/")]
    [InlineData("http://ha.example/")]
    [InlineData("http://10.0.0.1.example/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    [InlineData("http://10.1/")]
    [InlineData("http://167772161/")]
    [InlineData("http://0x0a000001/")]
    [InlineData("http://012.0.0.1/")]
    [InlineData("http://10.00.0.1/")]
    [InlineData("http://10.0.0.1./")]
    [InlineData("http://10.0.0.256/")]
    [InlineData("http://user:password@10.0.0.1/")]
    [InlineData("http://10.0.0.1/prefix/")]
    [InlineData("http://10.0.0.1/?query=value")]
    [InlineData("http://10.0.0.1/#fragment")]
    [InlineData("ftp://10.0.0.1/")]
    public async Task ExplicitHttpRejectsAnythingOutsideCanonicalPrivateIpv4Origin(string baseUrl)
    {
        using var host = CreateHost(Guid.NewGuid().ToString(), "BaseUrl", baseUrl, "true");
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal(HomeAssistantOptionsValidator.SafeMessage, Assert.Single(error.Failures));
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("http://10.1.2.3/", null)]
    [InlineData("http://10.1.2.3/", "false")]
    public async Task HttpRequiresExplicitTrueFlag(string baseUrl, string? allowHttp)
    {
        Assert.False(new HomeAssistantOptions().AllowHttp);
        using var host = CreateHost(Guid.NewGuid().ToString(), "BaseUrl", baseUrl, allowHttp);
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal(HomeAssistantOptionsValidator.SafeMessage, Assert.Single(error.Failures));
    }

    [Theory]
    [InlineData("https://ha.example/", null)]
    [InlineData("https://8.8.8.8/", "false")]
    [InlineData("https://127.0.0.1/", "true")]
    [InlineData("https://[::1]/", null)]
    [InlineData("https://[::ffff:192.168.1.2]/", "true")]
    public async Task HttpsAcceptanceDoesNotDependOnHttpException(string baseUrl, string? allowHttp)
    {
        // A HTTPS-ág nem használja a HTTP-kivételt; nincs szükség cím × flag szorzatra.
        using var host = CreateHost(Guid.NewGuid().ToString(), "BaseUrl", baseUrl, allowHttp);
        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task MalformedHttpFlagNeverEchoesRuntimeCanary()
    {
        var canary = Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var logs = new CapturingLogs();
        using var host = CreateHost(Guid.NewGuid().ToString(), allowHttp: canary, logs: logs);
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.True(!error.ToString().Contains(canary, StringComparison.Ordinal), "A konfigurációs hiba érzékeny bemenetet tartalmaz.");
        Assert.True(logs.Entries.All(entry => !entry.Message.Contains(canary, StringComparison.Ordinal) &&
            !(entry.Exception?.ToString().Contains(canary, StringComparison.Ordinal) ?? false)), "Érzékeny bemenet került a naplóba.");
        Assert.Equal(HomeAssistantOptionsValidator.SafeMessage, Assert.Single(error.Failures));
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Entries.Enqueue((logLevel, formatter(state, exception), exception));
        }
    }

    private static IHost CreateHost(string? id, string? key = null, string? value = null, string? allowHttp = null,
        ILoggerProvider? logs = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["HomeAssistant:BaseUrl"] = "https://ha.example/",
            ["HomeAssistant:TokenSecretId"] = id,
            ["HomeAssistant:Timeout"] = "00:00:02",
            ["HomeAssistant:DomainAllowlist:0"] = "sensor"
        };
        if (allowHttp is not null) config["HomeAssistant:AllowHttp"] = allowHttp;
        if (key is not null) config["HomeAssistant:" + key] = value;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (logs is not null) builder.Logging.AddProvider(logs);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.AddSingleton<ISecretProvider, NoIoSecrets>();
        builder.Services.AddHomeAssistantClient(builder.Configuration);
        return builder.Build();
    }

    private sealed class NoIoSecrets : ISecretProvider
    {
        public Task<string> GetSecretAsync(Guid secretId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Az indulás nem kérhet titkot.");
    }
}
