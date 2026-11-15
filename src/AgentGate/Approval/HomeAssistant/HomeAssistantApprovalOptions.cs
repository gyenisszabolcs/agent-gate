using System.Text.RegularExpressions;
using AgentGate.Integrations.HomeAssistant;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentGate.Approval.HomeAssistant;

public sealed class HomeAssistantApprovalOptions
{
    public const string SectionName = "HomeAssistantApproval";
    public const string SafeMessage = "A Home Assistant jóváhagyási konfiguráció érvénytelen.";
    public string? BaseUrl { get; set; }
    public bool AllowHttp { get; set; }
    public string? TokenSecretId { get; set; }
    public string? NotifyService { get; set; }
    public string? ExpectedUserId { get; set; }
    public string? ExpectedDeviceId { get; set; }
    public int TimeoutSeconds { get; set; } = 5;
    public int MaxIdleSeconds { get; set; } = 60;
    public bool ParsedCorrectly { get; set; } = true;

    public bool IsValid() => ParsedCorrectly && BaseUrl?.Length <= 2048 &&
        new HomeAssistantOptionsValidator().Validate(null, ToClientOptions()).Succeeded &&
        NotifyService is { Length: <= 135 } && Regex.IsMatch(NotifyService, @"\Anotify\.[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant) &&
        IsIdentifier(ExpectedUserId) && (ExpectedDeviceId is null || IsIdentifier(ExpectedDeviceId)) &&
        TimeoutSeconds is >= 1 and <= 30 && MaxIdleSeconds is >= 5 and <= 300;

    public static bool IsIdentifier(string? value) => value is { Length: >= 1 and <= 128 } &&
        Regex.IsMatch(value, @"\A[A-Za-z0-9_-]+\z", RegexOptions.CultureInvariant);

    public HomeAssistantOptions ToClientOptions() => new()
    {
        BaseUrl = BaseUrl,
        AllowHttp = AllowHttp,
        TokenSecretId = TokenSecretId,
        Timeout = TimeSpan.FromSeconds(TimeoutSeconds is >= 1 and <= 30 ? TimeoutSeconds : 1)
    };
}

public static class HomeAssistantApprovalRegistration
{
    public static IServiceCollection AddHomeAssistantApproval(this IServiceCollection services, IConfiguration configuration)
    {
        // GetChildren a megadott üres/null szakaszt is megkülönbözteti a hiányzótól.
        if (!configuration.GetChildren().Any(child => string.Equals(child.Key, HomeAssistantApprovalOptions.SectionName, StringComparison.OrdinalIgnoreCase)))
            return services;
        services.AddOptions<HomeAssistantApprovalOptions>().Configure(options =>
        {
            var section = configuration.GetSection(HomeAssistantApprovalOptions.SectionName);
            options.ParsedCorrectly = string.IsNullOrEmpty(section.Value);
            foreach (var child in section.GetChildren())
            {
                if (child.GetChildren().Any()) options.ParsedCorrectly = false;
                switch (child.Key.ToLowerInvariant())
                {
                    case "baseurl": options.BaseUrl = child.Value; break;
                    case "tokensecretid": options.TokenSecretId = child.Value; break;
                    case "notifyservice": options.NotifyService = child.Value; break;
                    case "expecteduserid": options.ExpectedUserId = child.Value; break;
                    case "expecteddeviceid": options.ExpectedDeviceId = child.Value; break;
                    case "allowhttp":
                        if (bool.TryParse(child.Value, out var allowHttp)) options.AllowHttp = allowHttp;
                        else options.ParsedCorrectly = false;
                        break;
                    case "timeoutseconds":
                        options.TimeoutSeconds = ParseInteger(child.Value);
                        break;
                    case "maxidleseconds":
                        options.MaxIdleSeconds = ParseInteger(child.Value);
                        break;
                    default: options.ParsedCorrectly = false; break;
                }
            }
        }).Validate(options => options.IsValid(), HomeAssistantApprovalOptions.SafeMessage).ValidateOnStart();
        services.TryAddSingleton<IApprovalWebSocketFactory, ApprovalWebSocketFactory>();
        services.TryAddSingleton<IApprovalHttpHandlerFactory, ApprovalHttpHandlerFactory>();
        services.AddHostedService(provider => new HomeAssistantHttpWarningService(
            Options.Create(provider.GetRequiredService<IOptions<HomeAssistantApprovalOptions>>().Value.ToClientOptions()),
            provider.GetRequiredService<ILogger<HomeAssistantHttpWarningService>>()));
        services.AddSingleton<HomeAssistantApprovalChannel>();
        services.AddSingleton<IApprovalChannel>(provider => provider.GetRequiredService<HomeAssistantApprovalChannel>());
        services.AddHostedService(provider => provider.GetRequiredService<HomeAssistantApprovalChannel>());
        return services;
    }

    private static int ParseInteger(string? value) => int.TryParse(value, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
}
