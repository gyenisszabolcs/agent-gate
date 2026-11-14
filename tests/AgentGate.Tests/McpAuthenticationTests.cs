using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace AgentGate.Tests;

public sealed class McpAuthenticationTests
{
    [Theory]
    [InlineData("GET", "missing")]
    [InlineData("POST", "missing")]
    [InlineData("DELETE", "missing")]
    [InlineData("POST", "wrong")]
    [InlineData("POST", "basic")]
    [InlineData("POST", "empty")]
    [InlineData("POST", "multiple")]
    public async Task InvalidCredentialsReturnUnauthorized(string method, string credential)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using var factory = new McpTestApplication(token);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/mcp");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        switch (credential)
        {
            case "wrong":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                break;
            case "basic":
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
                break;
            case "empty":
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer");
                break;
            case "multiple":
                request.Headers.TryAddWithoutValidation("Authorization", ["Bearer " + token, "Bearer " + token]);
                break;
        }
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await client.SendAsync(request, cancellation.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
        var body = await response.Content.ReadAsStringAsync(cancellation.Token);
        Assert.True(!body.Contains(token, StringComparison.Ordinal), "A válasz titkot tartalmaz.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingServerTokenFailsClosed(string? configuredToken)
    {
        await using var factory = new McpTestApplication(configuredToken);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await client.SendAsync(request, cancellation.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
