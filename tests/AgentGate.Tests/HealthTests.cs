using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AgentGate.Tests;

public sealed class HealthTests
{
    [Fact]
    public async Task GetHealthReturnsOk()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        using var response = await client.GetAsync("/health", cancellation.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(cancellation.Token));
    }
}
