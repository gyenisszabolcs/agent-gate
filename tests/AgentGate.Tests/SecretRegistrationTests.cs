using AgentGate.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentGate.Tests;

public sealed class SecretRegistrationTests
{
    [Fact]
    public async Task ProgramRegistersSingletonWithoutCredentialsOrHomeAssistant()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Secrets:SelfTestSecretId"] = null })));
        using var client = app.CreateClient();
        Assert.Null(app.Services.GetRequiredService<IConfiguration>()["Secrets:SelfTestSecretId"]);
        var provider = app.Services.GetService<ISecretProvider>();
        Assert.True(provider is BitwardenSecretProvider, "Hiányzik a Bitwarden regisztráció.");
        Assert.Same(provider, app.Services.GetRequiredService<ISecretProvider>());
        // A valódi példány forrásválasztását vizsgáljuk környezetmódosítás és natív SDK-hívás nélkül.
        var mode = typeof(BitwardenSecretProvider).GetFields(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(bool)).GetValue(provider);
        Assert.True(mode is true, "A gate nem kizárólag systemd credentialt használ.");
        Assert.Null(app.Services.GetService<AgentGate.Integrations.HomeAssistant.HomeAssistantClient>());
        Assert.Equal("ok", await client.GetStringAsync("/health"));
    }
}
