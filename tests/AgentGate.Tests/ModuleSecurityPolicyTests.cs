using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using AgentGate.Approval;
using AgentGate.Audit;
using AgentGate.Policy;
using AgentGate.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentGate.Tests;

public sealed class ModuleSecurityPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task RegisteredModuleErrorsUseOnlyExplicitCategoryAndOneSanitizedMessage(int failure)
    {
        var module = new ProbeModule { Failure = failure };
        await using var app = CreateApp(module);
        using var reply = await CallAsync(app, "module_control", new { value = "próba" });
        var text = Text(reply);
        var call = Assert.Single(await ReadCallsAsync(app), c => c.Tool == "module_control");
        Assert.Equal("Failed", call.State);
        Assert.True(text == call.SanitizedError, "Eltér a válasz és a tartós audit hibája.");
        Assert.True(!text.Contains(module.Canary, StringComparison.Ordinal), "Titok a hibaválaszban.");
        Assert.True(failure == 0 ? text == SecretRedactor.RedactText(module.SafeMessage) : text == "Az eszköz végrehajtása sikertelen.",
            "Nem a regisztrált hibakategória vagy az általános hiba jelent meg.");
    }

    [Fact]
    public async Task ModuleValidatesRawArgumentsBeforeSdkBinderAndEnrichesAudit()
    {
        var module = new ProbeModule();
        await using var app = CreateApp(module);
        using var reply = await CallAsync(app, "module_control", new { value = "próba", extra = "nem engedélyezett" });
        Assert.Equal("A modul argumentumai nem engedélyezettek.", Text(reply));
        Assert.Equal(0, module.Executions);
        var call = Assert.Single(await ReadCallsAsync(app));
        using var parameters = JsonDocument.Parse(call.SanitizedParameters);
        Assert.Equal("ellenőrzött", parameters.RootElement.GetProperty("module.category").GetString());
        Assert.False(parameters.RootElement.TryGetProperty("extra", out _));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("multiline")]
    [InlineData("below")]
    [InlineData("boundary")]
    [InlineData("above")]
    [InlineData("expansion")]
    public async Task SafeModuleCategoryIsSanitizedExactlyOnceAcrossTextBoundaries(string shape)
    {
        var message = shape switch
        {
            "empty" => "",
            "whitespace" => "   ",
            "multiline" => "A modul hibája.\nMásodik sor.",
            "below" => new string('x', 65_535),
            "boundary" => new string('x', 65_536),
            "above" => new string('x', 65_537),
            _ => string.Concat(Enumerable.Repeat("token=" + RandomNumberGenerator.GetInt32(10) + " ", 6000))
        };
        var module = new ProbeModule { SafeMessageOverride = message };
        await using var app = CreateApp(module);
        using var reply = await CallAsync(app, "module_control", new { value = "próba" });
        var expected = SecretRedactor.RedactText(message);
        var call = Assert.Single(await ReadCallsAsync(app));
        Assert.True(Text(reply) == expected && call.SanitizedError == expected, "Megváltozott az egyszer szűrt modulüzenet.");
        if (shape == "expansion")
            Assert.True(expected != SecretRedactor.RedactText(expected), "A teszt nem különbözteti meg az ismételt szűrést.");
    }

    [Fact]
    public async Task UnregisteredErrorCategoryFailsClosed()
    {
        var module = new ProbeModule();
        await using var app = CreateApp(module, registerErrors: false);
        using var reply = await CallAsync(app, "module_control", new { value = "próba" });
        Assert.Equal("Az eszköz végrehajtása sikertelen.", Text(reply));
        Assert.Equal("Az eszköz végrehajtása sikertelen.", Assert.Single(await ReadCallsAsync(app)).SanitizedError);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task RegisteredNonHaApprovalPreservesDecisionBoundaryAndSafeErrors(int failure, bool approve)
    {
        var module = new ProbeModule { Failure = failure };
        var channel = new ProbeChannel();
        await using var app = CreateApp(module, channel);
        using var reply = await CallAsync(app, "module_sensitive", new { value = "próba" });
        var notice = Assert.Single(channel.Notices);
        Assert.Contains("jóváhagyásra vár", Text(reply));
        Assert.Equal(0, module.Executions);
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(notice.Id,
            approve ? ApprovalChoice.Approve : ApprovalChoice.Reject, "admin", DateTimeOffset.UtcNow));
        var outcome = await channel.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(approve ? ApprovalState.Failed : ApprovalState.Rejected, outcome.State);
        Assert.Equal(approve ? 1 : 0, module.Executions);
        if (approve)
        {
            Assert.True(outcome.Result == (failure == 0 ? SecretRedactor.RedactText(module.SafeMessage) :
                "A jóváhagyott művelet vagy az audit lezárása sikertelen; ez nem visszagörgetés."), "Hibás jóváhagyási hibakategória.");
            var call = Assert.Single(await ReadCallsAsync(app), c => c.CallId == notice.Id);
            Assert.True(call.SanitizedError == outcome.Result, "Eltér a jóváhagyási válasz és az audit.");
        }
        await channel.Decisions.Writer.WriteAsync(new ApprovalDecision(notice.Id, ApprovalChoice.Approve, "admin", DateTimeOffset.UtcNow));
        using var status = await CallAsync(app, "gate_approval_status", new { requestId = notice.Id.ToString() });
        Assert.Equal(approve ? 1 : 0, module.Executions);
    }

    [Theory]
    [InlineData("module_forbidden", false)]
    [InlineData("module_unclassified", false)]
    [InlineData("module_control", true)]
    [InlineData("module_sensitive", true)]
    [InlineData("module_without_handler", false)]
    [InlineData("module_wrong_risk", false)]
    public async Task ModulePolicyCannotBypassRiskClassificationOrKillSwitch(string tool, bool killed)
    {
        var module = new ProbeModule();
        var channel = new ProbeChannel();
        await using var app = CreateApp(module, channel);
        using var client = app.CreateClient();
        if (killed) await File.WriteAllTextAsync(app.KillSwitchPath, "{\"Enabled\":true}");
        using var reply = await CallAsync(app, tool, new { value = "próba" });
        Assert.True(reply.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(0, module.Executions);
        Assert.Empty(channel.Notices);
        Assert.Single(await ReadCallsAsync(app));
    }

    private static McpTestApplication CreateApp(ProbeModule module, ProbeChannel? channel = null, bool registerErrors = true) => new(
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), services =>
        {
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton<IToolCallPolicy>(module);
            if (registerErrors) services.AddSingleton<IOperationErrorPolicy>(module);
            services.AddSingleton<IApprovalOperationHandler>(module);
            services.AddSingleton<IApprovalOperationHandler, WrongRiskHandler>();
            if (channel is not null) services.AddSingleton<IApprovalChannel>(channel);
            services.AddMcpServer().WithTools(module);
        });

    private static async Task<IReadOnlyList<AuditCall>> ReadCallsAsync(McpTestApplication app)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={app.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CallId, Tool, State, SanitizedParameters, SanitizedError FROM AuditCalls";
        var calls = new List<AuditCall>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            calls.Add(new AuditCall(Guid.Parse(reader.GetString(0)), default, "test-caller", reader.GetString(1),
                RiskLevel.Forbidden, reader.GetString(3), reader.GetString(2), null, reader.IsDBNull(4) ? null : reader.GetString(4)));
        return calls;
    }

    private static string Text(JsonDocument reply) => reply.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static async Task<JsonDocument> CallAsync(McpTestApplication app, string tool, object arguments)
    {
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            app.Services.GetRequiredService<IOptions<McpAuthenticationOptions>>().Value.Token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-11-25");
        using var response = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments }
        });
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = body.Split('\n').Last(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
        return JsonDocument.Parse(body);
    }

    public sealed class ProbeModule : IToolCallPolicy, IOperationErrorPolicy, IApprovalOperationHandler
    {
        public string Canary { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public string? SafeMessageOverride { get; init; }
        public string SafeMessage => SafeMessageOverride ?? "A modul művelete sikertelen. token=" + Canary;
        public int Failure { get; init; }
        public int Executions { get; private set; }
        public string Tool => "module_sensitive";
        public RiskLevel Risk => RiskLevel.SensitiveControl;
        public bool Handles(string tool) => tool is "module_control" or "module_sensitive";
        public void ValidateArguments(IReadOnlyDictionary<string, JsonElement> arguments)
        {
            if (arguments.Keys.Any(key => key != "value")) throw new AgentGate.Tools.GateToolException("A modul argumentumai nem engedélyezettek.");
        }
        public void EnrichAuditParameters(IReadOnlyDictionary<string, JsonElement> arguments, RiskLevel risk,
            IDictionary<string, string?> parameters) => parameters["module.category"] = "ellenőrzött";
        public string? GetSafeMessage(Exception exception, PreparedOperation? operation) =>
            exception is ModuleFailure && (operation is null || operation.Tool == Tool) ? SafeMessage : null;
        public PreparedOperation Prepare(IReadOnlyDictionary<string, JsonElement> arguments)
        {
            ValidateArguments(arguments);
            return new PreparedOperation(Tool, Risk, new Dictionary<string, string?> { ["value"] = arguments["value"].GetString() });
        }
        public Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Control(operation.Parameters["value"]!);
            return Task.FromResult("végrehajtva");
        }

        [McpServerTool(Name = "module_control"), ToolRisk(RiskLevel.Control), ToolAuditParameters("value")]
        public CallToolResult Control(string value)
        {
            Executions++;
            if (Failure == 4)
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = SafeMessage }] };
            throw Failure switch
            {
                0 => new ModuleFailure(),
                1 => new InvalidOperationException(SafeMessage),
                2 => new McpException(SafeMessage),
                _ => new InvalidOperationException(SafeMessage, new ModuleFailure())
            };
        }

        [McpServerTool(Name = "module_sensitive"), ToolRisk(RiskLevel.SensitiveControl), ToolAuditParameters("value")]
        public string Sensitive(string value) => throw new InvalidOperationException("Csak jóváhagyással.");

        [McpServerTool(Name = "module_forbidden"), ToolRisk(RiskLevel.Forbidden), ToolAuditParameters("value")]
        public string Forbidden(string value) { Executions++; return value; }

        [McpServerTool(Name = "module_unclassified"), ToolAuditParameters("value")]
        public string Unclassified(string value) { Executions++; return value; }

        [McpServerTool(Name = "module_without_handler"), ToolRisk(RiskLevel.SensitiveControl), ToolAuditParameters("value")]
        public string WithoutHandler(string value) { Executions++; return value; }

        [McpServerTool(Name = "module_wrong_risk"), ToolRisk(RiskLevel.System), ToolAuditParameters("value")]
        public string WrongRisk(string value) { Executions++; return value; }
    }

    public sealed class ModuleFailure : Exception;

    private sealed class WrongRiskHandler : IApprovalOperationHandler
    {
        public string Tool => "module_wrong_risk";
        public RiskLevel Risk => RiskLevel.SensitiveControl;
        public Task<string> ExecuteAsync(PreparedOperation operation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Eltérő kockázattal nem hajtható végre.");
    }

    public sealed class ProbeChannel : IApprovalChannel
    {
        public bool IsAvailable => true;
        public string Name => "teszt";
        public List<ApprovalNotice> Notices { get; } = [];
        public Channel<ApprovalDecision> Decisions { get; } = Channel.CreateUnbounded<ApprovalDecision>();
        public TaskCompletionSource<ApprovalOutcome> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendRequestAsync(ApprovalNotice request, CancellationToken cancellationToken) { Notices.Add(request); return Task.CompletedTask; }
        public IAsyncEnumerable<ApprovalDecision> ReadDecisionsAsync(CancellationToken cancellationToken) => Decisions.Reader.ReadAllAsync(cancellationToken);
        public Task SendOutcomeAsync(ApprovalOutcome outcome, CancellationToken cancellationToken) { Finished.TrySetResult(outcome); return Task.CompletedTask; }
    }
}
