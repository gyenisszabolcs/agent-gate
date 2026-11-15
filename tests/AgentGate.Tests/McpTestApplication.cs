using AgentGate.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentGate.Tests;

internal sealed class McpTestApplication(string? token, Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    private readonly string directory = Directory.CreateTempSubdirectory("agent-gate-mcp-").FullName;
    public string AuditPath => Path.Combine(directory, "audit.db");
    public string KillSwitchPath => Path.Combine(directory, "kill-switch.json");
    public bool InitializeKillSwitch { get; init; } = true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (InitializeKillSwitch)
        {
            File.WriteAllText(KillSwitchPath, "{\"Enabled\":false}");
        }
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KillSwitch:Path"] = KillSwitchPath,
            ["Audit:DatabasePath"] = AuditPath,
            ["Audit:Caller"] = "test-caller"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<McpAuthenticationOptions>(options => options.Token = token);
            configureServices?.Invoke(services);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
