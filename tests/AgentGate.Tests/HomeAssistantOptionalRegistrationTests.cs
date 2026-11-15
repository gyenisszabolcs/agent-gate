using System.Text;
using Microsoft.AspNetCore.Hosting;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantOptionalRegistrationTests
{
    [Fact]
    public async Task MissingSectionStartsWithoutClientOrSecretLookup()
    {
        using var host = CreateHost("{}");
        await host.StartAsync();
        Assert.Null(host.Services.GetService<HomeAssistantClient>());
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(), service => service.GetType().Name == "HomeAssistantHttpWarningService");
        await host.StopAsync();
    }

    [Theory]
    [InlineData("{\"HomeAssistant\":{}}")]
    [InlineData("{\"HomeAssistant\":null}")]
    [InlineData("{\"HomeAssistant\":\"\"}")]
    [InlineData("{\"HomeAssistant\":\"invalid\"}")]
    [InlineData("{\"HomeAssistant\":{\"AllowHttp\":false}}")]
    public async Task PresentInvalidSectionFailsSafely(string json)
    {
        using var host = CreateHost(json);
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal(HomeAssistantOptionsValidator.SafeMessage, Assert.Single(error.Failures));
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task ScalarSectionCannotBeHiddenByValidChildValuesFromAnotherSource()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HomeAssistant"] = "hibás",
            ["HomeAssistant:BaseUrl"] = "https://ha.example/",
            ["HomeAssistant:TokenSecretId"] = Guid.NewGuid().ToString()
        });
        builder.Services.AddSingleton<ISecretProvider, NoIoSecrets>();
        builder.Services.AddHomeAssistantClient(builder.Configuration);
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal(HomeAssistantOptionsValidator.SafeMessage, Assert.Single(error.Failures));
    }

    [Fact]
    public async Task ConfiguredRealProgramStartsWithoutFetchingTokenOrCallingHttp()
    {
        await using var factory = new McpTestApplication(null, services =>
        {
            services.AddSingleton<ISecretProvider, NoIoSecrets>();
            services.AddLogging(logging => logging.ClearProviders());
        });
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HomeAssistant:BaseUrl", "https://ha.invalid/");
            builder.UseSetting("HomeAssistant:TokenSecretId", Guid.NewGuid().ToString());
            builder.UseSetting("HomeAssistant:DomainAllowlist:0", "sensor");
            // A teszthost UseSetting másolása üres szülőértéket is létrehoz; valódi objektumként adjuk át.
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HomeAssistant"] = null,
                ["HomeAssistant:DomainAllowlist"] = null
            }));
        });
        using var client = configured.CreateClient();
        Assert.Equal("ok", await client.GetStringAsync("/health"));
        Assert.NotNull(configured.Services.GetService<HomeAssistantClient>());
        Assert.Empty(configured.Services.GetServices<AgentGate.Approval.IApprovalChannel>());
    }

    [Theory]
    [InlineData("AllowHttp", "{}")]
    [InlineData("AllowHttp", "{\"nested\":false}")]
    [InlineData("Timeout", "{\"nested\":\"00:00:02\"}")]
    [InlineData("DomainAllowlist", "\"sensor\"")]
    [InlineData("DomainAllowlist", "[[\"sensor\"]]")]
    public async Task InvalidOptionalValueShapesFailClosed(string key, string jsonValue)
    {
        using var value = System.Text.Json.JsonDocument.Parse(jsonValue);
        var settings = new Dictionary<string, object?>
        {
            ["BaseUrl"] = "https://ha.example/",
            ["TokenSecretId"] = Guid.NewGuid().ToString(),
            [key] = value.RootElement.Clone()
        };
        using var host = CreateHost(System.Text.Json.JsonSerializer.Serialize(new { HomeAssistant = settings }));
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Equal(HomeAssistantOptionsValidator.SafeMessage, Assert.Single(error.Failures));
    }

    private static IHost CreateHost(string json)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
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
