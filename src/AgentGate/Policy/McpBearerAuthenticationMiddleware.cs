using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AgentGate.Policy;

public sealed class McpBearerAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[]? _expectedTokenHash;

    public McpBearerAuthenticationMiddleware(RequestDelegate next, IOptions<McpAuthenticationOptions> options)
    {
        _next = next;
        var token = options.Value.Token;
        _expectedTokenHash = string.IsNullOrWhiteSpace(token) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) && !IsAuthorized(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }

        await _next(context);
    }

    private bool IsAuthorized(HttpContext context)
    {
        var values = context.Request.Headers.Authorization;
        if (_expectedTokenHash is null || values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter) || header.Parameter.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(header.Parameter));
        return CryptographicOperations.FixedTimeEquals(_expectedTokenHash, suppliedHash);
    }
}
