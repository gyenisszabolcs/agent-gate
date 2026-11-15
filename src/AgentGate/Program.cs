using AgentGate.Policy;
using AgentGate.Tools;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<GateTools>();
builder.Services.Configure<McpAuthenticationOptions>(options =>
    options.Token = Environment.GetEnvironmentVariable("GATE_MCP_TOKEN"));
var app = builder.Build();

app.UseMiddleware<McpBearerAuthenticationMiddleware>();

app.MapGet("/health", () => Results.Text("ok"));
app.MapMcp("/mcp");

await app.RunAsync();

public partial class Program;
