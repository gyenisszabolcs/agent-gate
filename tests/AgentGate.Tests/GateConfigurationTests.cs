using AgentGate.Approval.HomeAssistant;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentGate.Tests;

[CollectionDefinition("Gate configuration environment", DisableParallelization = true)]
public sealed class GateConfigurationCollection;

[Collection("Gate configuration environment")]
public sealed class GateConfigurationTests
{
    [Fact]
    public void MissingFilePreservesBuiltInAppsettings()
    {
        using var files = new ConfigurationFiles();
        var builder = files.CreateBuilder();
        GateConfiguration.AddStartupFile(builder.Configuration, files.Path);
        Assert.Equal("built-in-caller", builder.Configuration["Audit:Caller"]);
        Assert.Equal("15", builder.Configuration["Approval:LifetimeMinutes"]);
    }

    [Fact]
    public void FileOverridesAppsettingsAndRemainsAStartupSnapshot()
    {
        using var files = new ConfigurationFiles();
        File.WriteAllText(files.Path, """{"Audit":{"Caller":"file-caller"}}""");
        var builder = files.CreateBuilder();
        GateConfiguration.AddStartupFile(builder.Configuration, files.Path);
        Assert.Equal("file-caller", builder.Configuration["Audit:Caller"]);
        Assert.Equal("15", builder.Configuration["Approval:LifetimeMinutes"]);
        var provider = Assert.Single(((IConfigurationRoot)builder.Configuration).Providers.OfType<JsonConfigurationProvider>(),
            item => item.Source.Path == "agentgate.json");
        Assert.False(provider.Source.ReloadOnChange);
        File.WriteAllText(files.Path, """{"Audit":{"Caller":"changed-caller"}}""");
        Assert.Equal("file-caller", builder.Configuration["Audit:Caller"]);
        File.Delete(files.Path);
        Assert.Equal("file-caller", builder.Configuration["Audit:Caller"]);
    }

    [Fact]
    public void RealEnvironmentOverridesFileAndAppsettings()
    {
        const string name = "Audit__Caller";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "environment-caller");
            using var files = new ConfigurationFiles();
            File.WriteAllText(files.Path, """{"Audit":{"Caller":"file-caller"}}""");
            var builder = files.CreateBuilder();
            GateConfiguration.AddStartupFile(builder.Configuration, files.Path);
            Assert.Equal("environment-caller", builder.Configuration["Audit:Caller"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public void MalformedJsonFailsWithoutFileContentOrInnerException()
    {
        using var files = new ConfigurationFiles();
        var marker = Guid.NewGuid().ToString("N");
        File.WriteAllText(files.Path, "{\"Audit\":{\"Caller\":\"" + marker + "\"},\"" + marker + "\":");
        var builder = files.CreateBuilder();
        var error = Assert.ThrowsAny<Exception>(() => GateConfiguration.AddStartupFile(builder.Configuration, files.Path));
        Assert.Equal("A gate JSON konfigurációja érvénytelen.", error.Message);
        Assert.Null(error.InnerException);
        Assert.True(!error.ToString().Contains(marker, StringComparison.Ordinal), "A konfiguráció tartalma kiszivárgott.");
        Assert.Empty(error.Data);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("ConfigurationPath")]
    public void UnknownSectionFailsWithoutEchoingItsNameOrValue(string section)
    {
        using var files = new ConfigurationFiles();
        var marker = Guid.NewGuid().ToString("N");
        File.WriteAllText(files.Path, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [section + marker] = new { Value = marker }
        }));
        var builder = files.CreateBuilder();
        var error = Assert.Throws<InvalidOperationException>(() => GateConfiguration.AddStartupFile(builder.Configuration, files.Path));
        Assert.Equal("A gate JSON konfigurációja érvénytelen.", error.Message);
        Assert.Null(error.InnerException);
        Assert.True(!error.ToString().Contains(marker, StringComparison.Ordinal), "Ismeretlen szakasz tartalma kiszivárgott.");
    }

    [Theory]
    [InlineData("", "GATE_MCP_TOKEN", false)]
    [InlineData("", "AGENT_GATE_BITWARDEN_ACCESS_TOKEN", true)]
    [InlineData("HomeAssistant", "tOkEn", false)]
    [InlineData("Secrets", "AccessToken", true)]
    [InlineData("Audit", "Password", true)]
    [InlineData("HomeAssistantApproval", "api_key", false)]
    [InlineData("Audit", "Nested:Authorization", false)]
    public void SecretKeyPresenceFailsEvenWhenNullOrNested(string section, string key, bool nullValue)
    {
        using var files = new ConfigurationFiles();
        var marker = Guid.NewGuid().ToString("N");
        var value = nullValue ? null : marker;
        var entry = new Dictionary<string, object?> { [key] = value };
        var document = section.Length == 0 ? entry : new Dictionary<string, object?> { [section] = entry };
        File.WriteAllText(files.Path, System.Text.Json.JsonSerializer.Serialize(document));
        var builder = files.CreateBuilder();
        var error = Assert.Throws<InvalidOperationException>(() => GateConfiguration.AddStartupFile(builder.Configuration, files.Path));
        Assert.Equal("A gate JSON konfigurációja érvénytelen.", error.Message);
        Assert.Null(error.InnerException);
        Assert.True(!error.ToString().Contains(marker, StringComparison.Ordinal), "Tiltott konfigurációs érték kiszivárgott.");
        Assert.Empty(error.Data);
    }

    [Fact]
    public void ReadmeExamplePassesExistingValidatorsWithoutIo()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(System.IO.Path.Combine(root.FullName, "AgentGate.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var readme = File.ReadAllText(System.IO.Path.Combine(root.FullName, "README.md"));
        var example = Assert.Single(System.Text.RegularExpressions.Regex.Matches(readme, "```json\\r?\\n(?<json>[\\s\\S]*?)```"));
        using var files = new ConfigurationFiles();
        File.WriteAllText(files.Path, example.Groups["json"].Value);
        var builder = files.CreateBuilder();
        GateConfiguration.AddStartupFile(builder.Configuration, files.Path);
        var secrets = new HomeAssistantReadToolsTests.RuntimeSecrets();
        using var services = new ServiceCollection().AddLogging().AddSingleton(TimeProvider.System)
            .AddSingleton<ISecretProvider>(secrets).AddHomeAssistantClient(builder.Configuration)
            .AddHomeAssistantControl(builder.Configuration).AddHomeAssistantApproval(builder.Configuration).BuildServiceProvider();
        services.GetRequiredService<Microsoft.Extensions.Options.IStartupValidator>().Validate();
        Assert.True(services.GetRequiredService<ControlPolicy>().Enabled);
        Assert.True(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HomeAssistantApprovalOptions>>().Value.IsValid());
        Assert.True(Guid.TryParse(builder.Configuration["Secrets:SelfTestSecretId"], out var id) && id != Guid.Empty);
        Assert.Equal("/etc/agentgate/kill-switch.json", builder.Configuration["KillSwitch:Path"]);
        Assert.Equal(0, secrets.Calls);

        using var controlExample = (ConfigurationRoot)new ConfigurationBuilder()
            .AddJsonFile(System.IO.Path.Combine(root.FullName, "src/AgentGate/Integrations/HomeAssistant/Control/appsettings.example.jsonc")).Build();
        Assert.Equal("/etc/agentgate/kill-switch.json", controlExample["KillSwitch:Path"]);
    }

    private sealed class ConfigurationFiles : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("agent-gate-config-").FullName;
        private readonly List<ConfigurationManager> configurations = [];
        public string Path => System.IO.Path.Combine(directory, "agentgate.json");

        public WebApplicationBuilder CreateBuilder()
        {
            File.WriteAllText(System.IO.Path.Combine(directory, "appsettings.json"),
                """{"Audit":{"Caller":"built-in-caller"},"Approval":{"LifetimeMinutes":15}}""");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = directory,
                EnvironmentName = "Production"
            });
            configurations.Add(builder.Configuration);
            return builder;
        }

        public void Dispose()
        {
            foreach (IDisposable configuration in configurations) configuration.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RealProgramStartsWithOptionalFixedStartupFileBeforeEnvironment()
    {
        await using var factory = new McpTestApplication(null);
        using var client = factory.CreateClient();
        Assert.Equal("ok", await client.GetStringAsync("/health"));
        var providers = ((IConfigurationRoot)factory.Services.GetRequiredService<IConfiguration>()).Providers.ToArray();
        var file = Assert.Single(providers.OfType<JsonConfigurationProvider>(), provider =>
            provider.Source.FileProvider?.GetFileInfo(provider.Source.Path!).PhysicalPath == "/etc/agentgate/agentgate.json");
        Assert.True(file.Source.Optional);
        Assert.False(file.Source.ReloadOnChange);
        var environment = Assert.Single(providers.OfType<EnvironmentVariablesConfigurationProvider>(), provider =>
            provider.ToString() == "EnvironmentVariablesConfigurationProvider");
        Assert.True(Array.IndexOf(providers, file) < Array.IndexOf(providers, environment));
    }
}
