using System.Globalization;
using System.Text.RegularExpressions;
using AgentGate.Secrets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AgentGate.Integrations.HomeAssistant;

public sealed class HomeAssistantOptionsValidator : IValidateOptions<HomeAssistantOptions>
{
    public const string SafeMessage = "A Home Assistant konfiguráció érvénytelen.";

    public ValidateOptionsResult Validate(string? name, HomeAssistantOptions options)
    {
        var valid = Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host) &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/" &&
            Guid.TryParse(options.TokenSecretId, out var id) && id != Guid.Empty &&
            options.Timeout > TimeSpan.Zero && options.Timeout <= TimeSpan.FromMinutes(5) &&
            (options.DomainAllowlist ?? []).All(domain => domain is not null &&
                Regex.IsMatch(domain, @"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant));
        return valid ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(SafeMessage);
    }
}

public static class HomeAssistantServiceCollectionExtensions
{
    public static IServiceCollection AddHomeAssistantClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<HomeAssistantOptions>, HomeAssistantOptionsValidator>());
        services.AddOptions<HomeAssistantOptions>().Configure(options =>
        {
            var section = configuration.GetSection("HomeAssistant");
            options.BaseUrl = section["BaseUrl"];
            options.TokenSecretId = section["TokenSecretId"];
            options.DomainAllowlist = section.GetSection("DomainAllowlist").GetChildren().Select(child => child.Value!).ToArray();
            var timeout = section["Timeout"];
            if (timeout is not null)
                options.Timeout = TimeSpan.TryParse(timeout, CultureInfo.InvariantCulture, out var parsed) ? parsed : TimeSpan.Zero;
        }).ValidateOnStart();
        services.TryAddSingleton(provider => new HomeAssistantClient(
            provider.GetRequiredService<IOptions<HomeAssistantOptions>>(), provider.GetRequiredService<ISecretProvider>()));
        return services;
    }
}
