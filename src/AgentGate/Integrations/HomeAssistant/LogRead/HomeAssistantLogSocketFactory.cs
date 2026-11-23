using System.Net.WebSockets;

namespace AgentGate.Integrations.HomeAssistant.LogRead;

// Megbízható infrastruktúra, nem MCP-jog; a kliens birtokolja a factoryt és a visszaadott socketet.
public interface IHomeAssistantLogSocketFactory
{
    Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken);
}

internal sealed class HomeAssistantLogSocketFactory : IHomeAssistantLogSocketFactory, IDisposable
{
    private readonly HttpMessageInvoker invoker = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false
    });

    public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        try
        {
            await socket.ConnectAsync(uri, invoker, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw new HomeAssistantException("A Home Assistant naplókapcsolat sikertelen.");
        }
    }

    public void Dispose() => invoker.Dispose();
}
