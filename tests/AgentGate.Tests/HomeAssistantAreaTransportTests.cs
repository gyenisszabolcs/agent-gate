using System.Net;
using System.Text.Json;
using AgentGate.Tools;
using Xunit;

namespace AgentGate.Tests;

public sealed class HomeAssistantAreaTransportTests
{
    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"area_id\":\"a\",\"area_name\":\"A\"}]")]
    [InlineData("[{\"area_id\":\"a\",\"area_name\":null,\"entities\":[]}]")]
    [InlineData("[{\"area_id\":\"a\",\"area_name\":\"A\",\"entities\":[1]}]")]
    [InlineData("[{\"area_id\":\"a\",\"area_name\":\"A\",\"entities\":[]},{\"area_id\":\"a\",\"area_name\":\"B\",\"entities\":[]}]")]
    public async Task MalformedTemplateResponseFailsWithConstantSafeToolError(string body)
    {
        using var fixture = HomeAssistantAreaTests.CreateFixture(body);
        var error = await Assert.ThrowsAsync<GateToolException>(() => fixture.Tools.ListAreas(CancellationToken.None));
        Assert.Equal("A Home Assistant lekérés sikertelen.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateResponseKeepsSizeAndDepthBounds(bool deep)
    {
        var body = deep ? new string('[', 40) + "0" + new string(']', 40) : "[\"" + new string('x', 1024 * 1024) + "\"]";
        using var fixture = HomeAssistantAreaTests.CreateFixture(body);
        var error = await Assert.ThrowsAsync<GateToolException>(() => fixture.Tools.ListAreas(CancellationToken.None));
        Assert.Equal("A Home Assistant lekérés sikertelen.", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TemplateHttpFailureIsNotRetriedOrExposed(HttpStatusCode status)
    {
        using var fixture = HomeAssistantAreaTests.CreateFixture();
        var normal = fixture.Handler.Respond!;
        fixture.Handler.Respond = (request, ct) => request.RequestUri!.AbsolutePath != "/api/template" ? normal(request, ct) :
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(fixture.Secrets.Token),
                Headers = { Location = new Uri("https://other.example/") }
            });
        var error = await Assert.ThrowsAsync<GateToolException>(() => fixture.Tools.ListAreas(CancellationToken.None));
        Assert.Equal("A Home Assistant lekérés sikertelen.", error.Message);
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Fact]
    public async Task TemplateIsExactConstantAcrossCallerFiltersAndIndividualEntity()
    {
        using var fixture = HomeAssistantAreaTests.CreateFixture();
        var normal = fixture.Handler.Respond!;
        var templates = new List<string>();
        var deviceTemplates = new List<string>();
        fixture.Handler.Respond = async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/template")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://ha.example/api/template", request.RequestUri.AbsoluteUri);
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                Assert.True(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == fixture.Secrets.Token,
                    "A template-hívás hitelesítése hibás.");
                Assert.True(ct.CanBeCanceled);
                using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
                Assert.Equal("template", Assert.Single(document.RootElement.EnumerateObject()).Name);
                var template = document.RootElement.GetProperty("template").GetString()!;
                if (template.StartsWith("{% set entity_ids = ", StringComparison.Ordinal)) deviceTemplates.Add(template);
                else templates.Add(template);
            }
            return await normal(request, ct);
        };
        await fixture.Tools.ListStates(CancellationToken.None, "sensor", "{{ states('sensor.caller') }}");
        await fixture.Tools.ListStates(CancellationToken.None, "light", "Nappali");
        await fixture.Tools.GetState("sensor.room", CancellationToken.None);
        const string expected = """
            {% set ns = namespace(items=[]) %}
            {% for id in areas() %}
            {% set ns.items = ns.items + [{'area_id': id, 'area_name': area_name(id), 'entities': area_entities(id)}] %}
            {% endfor %}
            {{ ns.items | to_json }}
            """;
        Assert.Equal(3, templates.Count);
        Assert.All(templates, template => Assert.Equal(expected, template));
        Assert.Equal(2, deviceTemplates.Count);
        Assert.StartsWith("{% set entity_ids = [\"light.desk\"] %}\n", deviceTemplates[0]);
        Assert.StartsWith("{% set entity_ids = [\"sensor.room\"] %}\n", deviceTemplates[1]);
    }

    [Fact]
    public async Task TemplateTextDescriptorsUseExistingSecretSanitizer()
    {
        using var fixture = HomeAssistantAreaTests.CreateFixture();
        var normal = fixture.Handler.Respond!;
        fixture.Handler.Respond = (request, ct) => request.RequestUri!.AbsolutePath != "/api/template" ? normal(request, ct) :
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new[]
                {
                    new { area_id = "living", area_name = fixture.Secrets.Token, entities = new[] { "sensor.room" } },
                    new { area_id = "empty", area_name = "https://ha.example/?access_token=hidden", entities = Array.Empty<string>() }
                }))
            });
        var result = (await fixture.Tools.ListAreas(CancellationToken.None)).StructuredContent!.Value;
        Assert.False(result.GetRawText().Contains(fixture.Secrets.Token, StringComparison.Ordinal), "Titok a területválaszban.");
        Assert.False(result.GetRawText().Contains("access_token", StringComparison.Ordinal), "URL-hitelesítés a területválaszban.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateRequestHonorsTimeoutAndCallerCancellation(bool callerCancels)
    {
        using var fixture = new HomeAssistantReadToolsTests.Fixture("[]", timeout: callerCancels ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Respond = async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/template")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        };
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Tools.ListAreas(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (callerCancels)
        {
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<GateToolException>(() => pending);
            Assert.Equal("A Home Assistant lekérés sikertelen.", error.Message);
        }
        Assert.Equal(2, fixture.Handler.Calls);
    }
}
