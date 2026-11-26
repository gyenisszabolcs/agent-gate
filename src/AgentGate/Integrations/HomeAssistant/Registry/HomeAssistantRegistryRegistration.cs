using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Policy;
using AgentGate.Tools;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentGate.Integrations.HomeAssistant.Registry;

internal sealed class RegistryCallPolicy : IToolCallPolicy, IOperationErrorPolicy
{
    public bool Handles(string tool) => tool == HomeAssistantRegistryOperation.ToolName;
    public void ValidateArguments(IReadOnlyDictionary<string, JsonElement> arguments) => HomeAssistantRegistryService.ValidateArguments(arguments);
    public void EnrichAuditParameters(IReadOnlyDictionary<string, JsonElement> arguments, RiskLevel risk, IDictionary<string, string?> parameters) { }
    public string? GetSafeMessage(Exception exception, PreparedOperation? operation) => exception switch
    {
        RegistryPrestateChangedException => RegistryPrestateChangedException.SafeMessage,
        GateToolException when operation?.Tool == HomeAssistantRegistryOperation.ToolName =>
            "A nyilvántartási művelet nem engedélyezett; új kérés és jóváhagyás szükséges.",
        _ => null
    };
}

public static class HomeAssistantRegistryRegistration
{
    public static IServiceCollection AddHomeAssistantRegistry(this IServiceCollection services, IConfiguration configuration)
    {
        // A konfiguráció szigorúan ellenőrzött, hiányában az írás tiltott.
        services.TryAddSingleton(RegistryPolicy.FromConfiguration(configuration));
        services.TryAddSingleton(sp => new HomeAssistantRegistryService(sp.GetService<HomeAssistantClient>(), sp.GetRequiredService<RegistryPolicy>(), sp.GetRequiredService<IKillSwitchStateSource>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IApprovalOperationHandler, HomeAssistantRegistryOperation>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IToolCallPolicy, RegistryCallPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationErrorPolicy, RegistryCallPolicy>());
        return services;
    }
}
