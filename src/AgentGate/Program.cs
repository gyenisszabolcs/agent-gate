using AgentGate.Audit;
using Microsoft.Extensions.Options;
using AgentGate.Policy;
using AgentGate.Tools;
using AgentGate.Secrets;
using AgentGate.Approval;
using AgentGate.Approval.HomeAssistant;
using AgentGate.Integrations.HomeAssistant;

var builder = WebApplication.CreateBuilder(args);
// Az SDK 2.4.0 a filteren kívül nyers eszköznevet, Trace szinten teljes üzenettartalmat naplóz.
// Az alkalmazás/ASP.NET naplója megmarad; az SDK szerverdiagnosztikáját a tartós audit váltja ki.
builder.Services.PostConfigure<LoggerFilterOptions>(options =>
{
    for (var index = 0; index < options.Rules.Count; index++)
    {
        var rule = options.Rules[index];
        // A providerspecifikus vagy szűkebb konfiguráció sem engedheti vissza a nyers SDK-adatot.
        options.Rules[index] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, rule.LogLevel,
            (provider, category, level) =>
                category?.StartsWith("ModelContextProtocol.Server.", StringComparison.Ordinal) != true &&
                (rule.Filter?.Invoke(provider, category, level) ?? true));
    }
    options.Rules.Add(new LoggerFilterRule(null, "ModelContextProtocol.Server.", LogLevel.None, null));
});
builder.Services.AddHostedService<SecretSelfTestService>();
builder.Services.AddSingleton<ISecretProvider>(_ => new BitwardenSecretProvider(credentialOnly: true));
builder.Services.Configure<AuditOptions>(builder.Configuration.GetSection("Audit"));
builder.Services.AddSingleton<IAuditStore>(services => new SqliteAuditStore(
    services.GetRequiredService<IOptions<AuditOptions>>().Value.DatabasePath));
builder.Services.Configure<KillSwitchFileOptions>(builder.Configuration.GetSection("KillSwitch"));
builder.Services.AddSingleton<IKillSwitchStateSource, FileKillSwitchStateSource>();
builder.Services.AddSingleton<McpAuditFilter>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHomeAssistantClient(builder.Configuration);
builder.Services.AddHomeAssistantApproval(builder.Configuration);
builder.Services.AddSingleton<IApprovalOperationHandler, ApprovalTestOperation>();
builder.Services.AddSingleton<ApprovalGate>();
builder.Services.AddOptions<ApprovalGateOptions>()
    .Configure<IConfiguration>((options, configuration) =>
    {
        static int Read(IConfiguration configuration, string name, int fallback) => configuration[name] is not { } value
            ? fallback : int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
        options.LifetimeMinutes = Read(configuration, "Approval:LifetimeMinutes", 15);
        options.SendTimeoutSeconds = Read(configuration, "Approval:SendTimeoutSeconds", 5);
    })
    .Validate(options => options.LifetimeMinutes is >= 1 and <= 1440 && options.SendTimeoutSeconds is >= 1 and <= 30,
        "Érvénytelen jóváhagyási konfiguráció.")
    .ValidateOnStart();
builder.Services.AddSingleton<InMemoryApprovalStore>();
builder.Services.AddHostedService(services => services.GetRequiredService<ApprovalGate>());
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<GateTools>()
    .WithTools<ApprovalTools>()
    .WithTools<HomeAssistantTools>()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => (request, cancellationToken) =>
        request.Services!.GetRequiredService<McpAuditFilter>().InvokeAsync(request, next, cancellationToken)));
builder.Services.Configure<McpAuthenticationOptions>(options =>
    options.Token = Environment.GetEnvironmentVariable("GATE_MCP_TOKEN"));
var app = builder.Build();

app.UseMiddleware<McpBearerAuthenticationMiddleware>();

app.MapGet("/health", () => Results.Text("ok"));
app.MapMcp("/mcp");

await app.RunAsync();

public partial class Program;
