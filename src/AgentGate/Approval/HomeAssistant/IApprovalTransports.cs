using System.Net.WebSockets;

namespace AgentGate.Approval.HomeAssistant;

public interface IApprovalWebSocketFactory
{
    Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken);
}

public interface IApprovalHttpHandlerFactory
{
    HttpMessageHandler CreateHandler();
}
