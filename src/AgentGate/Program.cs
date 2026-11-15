using AgentGate.Audit;
using Microsoft.Extensions.Options;
using AgentGate.Policy;
using AgentGate.Tools;

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
builder.Services.Configure<AuditOptions>(builder.Configuration.GetSection("Audit"));
builder.Services.AddSingleton<IAuditStore>(services => new SqliteAuditStore(
    services.GetRequiredService<IOptions<AuditOptions>>().Value.DatabasePath));
builder.Services.Configure<KillSwitchFileOptions>(builder.Configuration.GetSection("KillSwitch"));
builder.Services.AddSingleton<IKillSwitchStateSource, FileKillSwitchStateSource>();
builder.Services.AddSingleton<McpAuditFilter>();
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<GateTools>()
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
