using AgentGate.Integrations.HomeAssistant;
using AgentGate.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    private static IHost CreateHost(string? id, string? key = null, string? value = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["HomeAssistant:BaseUrl"] = "https://ha.example/",
            ["HomeAssistant:TokenSecretId"] = id,
            ["HomeAssistant:Timeout"] = "00:00:02",
            ["HomeAssistant:DomainAllowlist:0"] = "sensor"
        };
        if (key is not null) config["HomeAssistant:" + key] = value;
        var builder = Host.CreateApplicationBuilder();
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
