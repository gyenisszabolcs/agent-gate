using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
            (uri.Scheme == Uri.UriSchemeHttps ||
                (options.AllowHttp && uri.Scheme == Uri.UriSchemeHttp && IsPrivateIpv4Literal(uri))) &&
            !string.IsNullOrEmpty(uri.Host) &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/" &&
            Guid.TryParse(options.TokenSecretId, out var id) && id != Guid.Empty &&
            options.Timeout > TimeSpan.Zero && options.Timeout <= TimeSpan.FromMinutes(5) &&
            (options.DomainAllowlist ?? []).All(domain => domain is not null &&
                Regex.IsMatch(domain, @"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant));
        return valid ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(SafeMessage);
    }

    private static bool IsPrivateIpv4Literal(Uri uri)
    {
        // Az eredeti authority kell: a Uri rövidített, hexadecimális és oktális címeket is normalizálhat.
        // Kizárólag négyrészes, kanonikus decimális IPv4-literál; nincs DNS-feloldás.
        var match = Regex.Match(uri.OriginalString, @"\Ahttp://(?<host>[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)(?::[0-9]+)?/?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !IPAddress.TryParse(match.Groups["host"].Value, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != match.Groups["host"].Value)
            return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
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
            var allowHttp = section["AllowHttp"];
            if (allowHttp is not null)
            {
                if (!bool.TryParse(allowHttp, out var parsedAllowHttp))
                    throw new OptionsValidationException(Options.DefaultName, typeof(HomeAssistantOptions),
                        [HomeAssistantOptionsValidator.SafeMessage]);
                options.AllowHttp = parsedAllowHttp;
            }
            options.TokenSecretId = section["TokenSecretId"];
            options.DomainAllowlist = section.GetSection("DomainAllowlist").GetChildren().Select(child => child.Value!).ToArray();
            var timeout = section["Timeout"];
            if (timeout is not null)
                options.Timeout = TimeSpan.TryParse(timeout, CultureInfo.InvariantCulture, out var parsed) ? parsed : TimeSpan.Zero;
        }).ValidateOnStart();
        services.AddHostedService<HomeAssistantHttpWarningService>();
        services.TryAddSingleton(provider => new HomeAssistantClient(
            provider.GetRequiredService<IOptions<HomeAssistantOptions>>(), provider.GetRequiredService<ISecretProvider>()));
        return services;
    }
}
