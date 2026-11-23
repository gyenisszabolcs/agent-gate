using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentGate.Integrations.HomeAssistant.LogRead;

namespace AgentGate.Tests;

// Minden kapcsolat saját, szkriptelt socket. Soha nem nyit hálózatot.
internal sealed class HomeAssistantLogTestSocketFactory(Func<string> body, Func<string> token) : IHomeAssistantLogSocketFactory
{
    public int Calls { get; private set; }
    public Uri? Target { get; private set; }
    public HomeAssistantLogTestSocket? Last { get; private set; }
    public Func<CancellationToken, Task>? BeforeConnect { get; set; }
    public Func<int, CancellationToken, Task>? BeforeReceive { get; set; }
    public Func<int, byte[]>? Frame { get; set; }
    public int FragmentSize { get; set; } = 8_192;
    public bool NeverEnd { get; set; }
    public WebSocketMessageType MessageType { get; set; } = WebSocketMessageType.Text;
    public bool FixedRequest => Last is { Sent.Count: 2 } &&
        Last.Sent[0].GetProperty("type").GetString() == "auth" && Last.Sent[0].GetProperty("access_token").GetString() == token() &&
        Last.Sent[1].GetProperty("type").GetString() == "system_log/list" && Last.Sent[1].GetProperty("id").GetInt32() == 1;

    public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        Calls++;
        Target = uri;
        if (BeforeConnect is not null) await BeforeConnect(cancellationToken);
        Last = new HomeAssistantLogTestSocket(this, body);
        return Last;
    }

    internal sealed class HomeAssistantLogTestSocket(HomeAssistantLogTestSocketFactory owner, Func<string> body) : WebSocket
    {
        private int frame;
        private int offset;
        private byte[]? current;
        public List<JsonElement> Sent { get; } = [];
        public bool Disposed { get; private set; }
        public bool Aborted { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => Aborted = true;
        public override void Dispose() => Disposed = true;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var json = JsonDocument.Parse(buffer.AsMemory());
            Sent.Add(json.RootElement.Clone());
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (owner.BeforeReceive is not null) await owner.BeforeReceive(frame, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            current ??= owner.Frame?.Invoke(frame) ?? Encoding.UTF8.GetBytes(frame switch
            {
                0 => "{\"type\":\"auth_required\",\"ha_version\":\"mock\"}",
                1 => "{\"type\":\"auth_ok\",\"ha_version\":\"mock\"}",
                _ => "{\"id\":1,\"type\":\"result\",\"success\":true,\"result\":" + body() + "}"
            });
            var count = Math.Min(Math.Min(buffer.Count, owner.FragmentSize), current.Length - offset);
            current.AsSpan(offset, count).CopyTo(buffer.AsSpan());
            offset += count;
            var end = offset == current.Length && !owner.NeverEnd;
            if (end) { frame++; offset = 0; current = null; }
            return new WebSocketReceiveResult(count, owner.MessageType, end);
        }
    }
}
