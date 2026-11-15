using System.Net.WebSockets;

namespace AgentGate.Approval.HomeAssistant;

public sealed class ApprovalHttpHandlerFactory : IApprovalHttpHandlerFactory
{
    public HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false
    };
}

public sealed class ApprovalWebSocketFactory : IApprovalWebSocketFactory, IDisposable
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
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        try
        {
            await socket.ConnectAsync(uri, invoker, cancellationToken);
            return socket;
        }
        catch (Exception)
        {
            socket.Dispose();
            throw new InvalidOperationException("A Home Assistant jóváhagyási kapcsolat nem hozható létre.");
        }
    }

    public void Dispose() => invoker.Dispose();
}
