using System.Security.Cryptography;
using AgentGate.Approval;
using AgentGate.Secrets;
using AgentGate.Approval.HomeAssistant;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantApprovalRegistrationTests
{
    [Theory]
    [InlineData("http://ha.invalid", true)]
    [InlineData("http://10.1.2.3", false)]
    [InlineData("http://8.8.8.8", true)]
    [InlineData("http://127.0.0.1", true)]
    [InlineData("http://169.254.1.2", true)]
    [InlineData("http://[::1]", true)]
    [InlineData("http://[fd00::1]", true)]
    [InlineData("http://010.1.2.3", true)]
    [InlineData("http://0x0a010203", true)]
    [InlineData("http://10.1.2", true)]
    [InlineData("http://10.1.2.3?other=1", true)]
    [InlineData("http://10.1.2.3/path", true)]
    [InlineData("https://ha.invalid#fragment", false)]
    [InlineData("https://user@ha.invalid", false)]
    public void InvalidOriginsCannotBypassExistingHomeAssistantBoundary(string baseUrl, bool allowHttp)
    {
        var options = new HomeAssistantApprovalOptions
        {
            BaseUrl = baseUrl,
            AllowHttp = allowHttp,
            TokenSecretId = Guid.NewGuid().ToString(),
            NotifyService = "notify.mobile_app_test",
            ExpectedUserId = "admin"
        };
        Assert.False(options.IsValid());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("scalar")]
    [InlineData("inline-token")]
    [InlineData("bad-guid")]
    [InlineData("empty-guid")]
    [InlineData("bad-bool")]
    [InlineData("bad-timeout")]
    [InlineData("bad-idle")]
    [InlineData("nested")]
    [InlineData("bad-notify")]
    public async Task InvalidSectionsFailRealStartupWithoutEchoOrExternalDependency(string kind)
    {
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var values = new Dictionary<string, string?>
        {
            ["HomeAssistantApproval:BaseUrl"] = "https://ha.invalid",
            ["HomeAssistantApproval:TokenSecretId"] = Guid.NewGuid().ToString(),
            ["HomeAssistantApproval:NotifyService"] = "notify.mobile_app_test",
            ["HomeAssistantApproval:ExpectedUserId"] = "admin"
        };
        switch (kind)
        {
            case "empty": values.Clear(); values["HomeAssistantApproval"] = ""; break;
            case "scalar": values["HomeAssistantApproval"] = canary; break;
            case "inline-token": values["HomeAssistantApproval:Token"] = canary; break;
            case "bad-guid": values["HomeAssistantApproval:TokenSecretId"] = canary; break;
            case "empty-guid": values["HomeAssistantApproval:TokenSecretId"] = Guid.Empty.ToString(); break;
            case "bad-bool": values["HomeAssistantApproval:AllowHttp"] = canary; break;
            case "bad-timeout": values["HomeAssistantApproval:TimeoutSeconds"] = "0"; break;
            case "bad-idle": values["HomeAssistantApproval:MaxIdleSeconds"] = "301"; break;
            case "nested": values["HomeAssistantApproval:BaseUrl:child"] = canary; break;
            case "bad-notify": values["HomeAssistantApproval:NotifyService"] = "notify/" + canary; break;
        }
        var provider = new NoSecrets();
        var errors = new StartupErrors();
        await using var app = CreateApp(provider, values, errors);
        AssertInvalidStartup(app, canary, errors);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void ValidLowercaseConfigurationBindsLikeDotNetConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["homeassistantapproval:baseurl"] = "https://ha.invalid",
            ["homeassistantapproval:tokensecretid"] = Guid.NewGuid().ToString(),
            ["homeassistantapproval:notifyservice"] = "notify.mobile_app_test",
            ["homeassistantapproval:expecteduserid"] = "admin"
        };
        using var services = new ServiceCollection().AddHomeAssistantApproval(new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
        var error = Record.Exception(() => services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HomeAssistantApprovalOptions>>().Value);
        Assert.True(error is null, "Az érvényes, kisbetűs konfiguráció elutasított.");
    }

    [Fact]
    public async Task ConfiguredInvalidChannelFailsProgramStartupWithFixedMessageAndNoSecretLookup()
    {
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var provider = new NoSecrets();
        var errors = new StartupErrors();
        await using var app = CreateApp(provider, new Dictionary<string, string?>
        {
            ["HomeAssistantApproval:BaseUrl"] = canary
        }, errors);
        AssertInvalidStartup(app, canary, errors);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task MissingSectionStartsRealProgramWithoutChannelOrSecretCalls()
    {
        var provider = new NoSecrets();
        await using var app = CreateApp(provider, new Dictionary<string, string?>());
        using var client = app.CreateClient();
        Assert.Equal("ok", await client.GetStringAsync("/health"));
        Assert.Empty(app.Services.GetServices<IApprovalChannel>());
        Assert.Equal(0, provider.Calls);
    }

    private static void AssertInvalidStartup(WebApplicationFactory<Program> app, string canary, StartupErrors errors)
    {
        var error = Record.Exception(() => app.CreateClient());
        Assert.True(error is not null && !error.ToString().Contains(canary, StringComparison.Ordinal), "A konfigurációhiba hiányzik vagy bemenetet szivárogtat.");
        // A DeferredHost.StartAsync az időközben leállt hostból kérhet lifetime-ot;
        // ekkor a tényleges startuphibát a valódi host naplójából is ellenőrizzük.
        var cause = error is ObjectDisposedException ? errors.Exceptions.FirstOrDefault(item => item is Microsoft.Extensions.Options.OptionsValidationException) : error;
        Assert.True(cause is not null && cause.ToString().Contains(HomeAssistantApprovalOptions.SafeMessage, StringComparison.Ordinal) &&
            !cause.ToString().Contains(canary, StringComparison.Ordinal), "Nem igazolt a fix, szanitizált konfigurációhiba.");
    }

    private static WebApplicationFactory<Program> CreateApp(NoSecrets provider, Dictionary<string, string?> configuration, StartupErrors? errors = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            configuration["Secrets:SelfTestSecretId"] = null;
            foreach (var (key, value) in configuration)
                builder.UseSetting(key, value);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration));
            builder.ConfigureTestServices(services =>
            {
                services.AddLogging(logging => logging.ClearProviders());
                if (errors is not null) services.AddLogging(logging => logging.AddProvider(errors));
                services.AddSingleton<ISecretProvider>(provider);
            });
        });

    private sealed class StartupErrors : ILoggerProvider, ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<Exception> Exceptions { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Exceptions.Enqueue(exception);
        }
    }

    private sealed class NoSecrets : ISecretProvider
    {
        public int Calls;
        public Task<string> GetSecretAsync(Guid secretId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("A teszt nem érhet el titokszolgáltatást.");
        }
    }
}
