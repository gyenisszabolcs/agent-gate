using Microsoft.Extensions.Options;

namespace AgentGate.Integrations.HomeAssistant;

internal sealed class HomeAssistantHttpWarningService(
    IOptions<HomeAssistantOptions> options,
    ILogger<HomeAssistantHttpWarningService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.AllowHttp)
            logger.LogWarning("A Home Assistant HTTP-kivétel engedélyezett; a titkosítatlan kapcsolat Bearer tokent tehet hozzáférhetővé.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
