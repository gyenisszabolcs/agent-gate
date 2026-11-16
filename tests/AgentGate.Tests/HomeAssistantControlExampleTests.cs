using AgentGate.Approval.HomeAssistant;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantControlExampleTests
{
    [Fact]
    public void CompleteExamplePassesFinalProductionParsersAndValidatorsWithoutIo()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentGate.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "src/AgentGate/Integrations/HomeAssistant/Control/appsettings.example.jsonc");
        Assert.True(File.Exists(path), "Hiányzik a teljes vezérlési konfigurációs példa.");
        using var config = (ConfigurationRoot)new ConfigurationBuilder().AddJsonFile(path).Build();
        var secrets = new HomeAssistantReadToolsTests.RuntimeSecrets();
        using var provider = new ServiceCollection().AddLogging(l => l.ClearProviders()).AddSingleton(TimeProvider.System)
            .AddSingleton<ISecretProvider>(secrets).AddHomeAssistantClient(config).AddHomeAssistantControl(config).AddHomeAssistantApproval(config).BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
        Assert.True(provider.GetRequiredService<ControlPolicy>().Enabled);
        var client = provider.GetRequiredService<HomeAssistantClient>();
        Assert.True(client.IsDomainAllowed("lock"));
        Assert.True(provider.GetRequiredService<IOptions<HomeAssistantApprovalOptions>>().Value.IsValid());
        var policy = provider.GetRequiredService<ControlPolicy>();
        foreach (var row in HomeAssistantControlPolicyTests.Matrix())
        {
            var domain = (string)row[0];
            if (domain == "switch") continue;
            policy.Validate(HomeAssistantControlPolicyTests.Arguments(domain, (string)row[1], (string)row[3]), (AgentGate.Policy.RiskLevel)row[2]);
        }
        Assert.Throws<AgentGate.Tools.GateToolException>(() => policy.Validate(HomeAssistantControlPolicyTests.Arguments("switch", "turn_on"), AgentGate.Policy.RiskLevel.Control));
        Assert.Equal(0, secrets.Calls);
    }
}
