using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Integrations.HomeAssistant;
using AgentGate.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantControlFailureBoundaryTests
{
    public static IEnumerable<object[]> ProviderFailures()
    {
        foreach (var sensitive in new[] { false, true })
        {
            foreach (var stage in new[] { "secret", "fresh" })
                foreach (var kind in new[] { "raw", "gate", "ha", "mcp" })
                    yield return [sensitive, stage, kind];
            foreach (var kind in new[] { "gate", "http", "response" })
                yield return [sensitive, "post", kind];
            yield return [sensitive, "readback", "gate"];
        }
    }

    private static Exception ProviderError(string kind, string canary) => kind switch
    {
        "gate" => new GateToolException(canary),
        "ha" => new HomeAssistantException(canary),
        "mcp" => new McpException(canary),
        "http" => new HttpRequestException("https://ha.example/" + canary + " X-Provider: " + canary, new InvalidOperationException(canary)),
        _ => new InvalidOperationException(canary)
    };

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public async Task ProviderMessagesNeverBecomeTrustedControlFailures(bool sensitive, string stage, string kind)
    {
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var logs = new Audit.McpAuditTests.RecordingLogger();
        await using var f = new ControlFixture(services => services.AddLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)))
        {
            Entity = "cover.room",
            Attributes = JsonSerializer.SerializeToElement(new { device_class = sensitive ? "garage" : "blind" })
        };
        Guid? id = null;
        if (sensitive)
        {
            using var pending = await f.Call("ha_sensitive_control", "cover", "open_cover");
            Assert.False(ControlFixture.Failed(pending));
            id = Assert.Single(f.Channel.Requests).Id;
        }
        if (stage == "secret") f.Secrets.Read = _ => throw ProviderError(kind, canary);
        f.Respond = (request, _) =>
        {
            var fail = stage == "fresh" && request.Method == HttpMethod.Get ||
                stage == "post" && request.Method == HttpMethod.Post ||
                stage == "readback" && request.Method == HttpMethod.Get && f.Posts > 0;
            if (!fail) return Task.FromResult(f.Response(request));
            if (kind != "response") throw ProviderError(kind, canary);
            var response = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(canary) };
            response.Headers.TryAddWithoutValidation("X-Provider", canary);
            response.Headers.Location = new Uri("https://ha.example/" + canary);
            return Task.FromResult(response);
        };
        string output;
        if (sensitive)
        {
            var outcome = await f.Channel.Approve();
            Assert.Equal(ApprovalState.Failed, outcome.State);
            output = JsonSerializer.Serialize(outcome);
            using var status = await HomeAssistantToolProtocolTests.SendAsync(f.Client, "tools/call", new { name = "gate_approval_status", arguments = new { requestId = id.ToString() } });
            output += status.RootElement.GetRawText();
        }
        else
        {
            using var reply = await f.Call("ha_control", "cover", "open_cover");
            Assert.True(ControlFixture.Failed(reply));
            output = reply.RootElement.GetRawText();
        }
        Assert.Equal(stage is "post" or "readback" ? 1 : 0, f.Posts);
        Assert.False(output.Contains(canary, StringComparison.Ordinal) || output.Contains(f.Secrets.Token, StringComparison.Ordinal), "Szolgáltatói adat a válaszban, státuszban vagy értesítésben.");
        Assert.False(logs.Contains(canary) || logs.Contains(f.Secrets.Token), "Szolgáltatói adat a strukturált naplóban.");
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.App.AuditPath};Pooling=False");
        await db.OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT SanitizedParameters, SanitizedError, State FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var text = reader.GetValue(i).ToString()!;
                Assert.False(text.Contains(canary, StringComparison.Ordinal) || text.Contains(f.Secrets.Token, StringComparison.Ordinal), "Szolgáltatói adat a tartós auditban.");
                Assert.NotEqual("Pending", text);
            }
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("gate")]
    [InlineData("ha")]
    [InlineData("mcp")]
    [InlineData("http")]
    public async Task ClientDropsOriginalProviderExceptionIncludingInnerException(string kind)
    {
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var f = new ControlFixture();
        f.Secrets.Read = _ => throw ProviderError(kind, canary);
        var client = f.App.Services.GetRequiredService<HomeAssistantClient>();
        var error = await Record.ExceptionAsync(() => client.GetStateAsync("cover.room"));
        Assert.True(error is HomeAssistantException && error.InnerException is null, "A szolgáltatói kivétel típusa vagy belső kivétele átjutott.");
        Assert.False(error.ToString().Contains(canary, StringComparison.Ordinal), "Az eredeti szolgáltatói adat átjutott a kliensen.");
        Assert.Equal(0, f.Posts);
    }
}
