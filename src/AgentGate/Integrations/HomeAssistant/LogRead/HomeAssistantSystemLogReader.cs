using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentGate.Secrets;

namespace AgentGate.Integrations.HomeAssistant.LogRead;

internal static class HomeAssistantSystemLogReader
{
    private const int MaximumMessageBytes = 1_048_576;
    private const int MaximumTotalBytes = MaximumMessageBytes + 8_192;
    private const int MaximumFragments = 256;

    public static async Task<HomeAssistantLogSnapshot> ReadAsync(IHomeAssistantLogSocketFactory sockets, Uri origin,
        ISecretProvider secrets, Guid secretId, TimeSpan timeout, string? level, string? integration, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct = deadline.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            var lease = await secrets.GetSecretLeaseAsync(secretId, ct);
            var token = lease.Value;
            if (string.IsNullOrEmpty(token) || token.Length > 16_384 ||
                !Regex.IsMatch(token, @"\A[A-Za-z0-9\-._~+/]+=*\z", RegexOptions.CultureInvariant))
                throw new InvalidOperationException();
            var target = new UriBuilder(origin)
            {
                Scheme = origin.Scheme == "https" ? "wss" : "ws", Path = "/api/websocket", Query = "", Fragment = ""
            }.Uri;
            using var socket = await sockets.ConnectAsync(target, ct);
            var bytes = 0;
            try
            {
                using var required = await ReceiveAsync(4_096);
                if (Type(required) != "auth_required") throw new InvalidOperationException();
                await SendAsync(new { type = "auth", access_token = token });
                using var auth = await ReceiveAsync(4_096);
                if (Type(auth) == "auth_invalid")
                {
                    secrets.Invalidate(secretId, lease);
                    throw new InvalidOperationException();
                }
                if (Type(auth) != "auth_ok") throw new InvalidOperationException();
                await SendAsync(new { id = 1, type = "system_log/list" });
                using var result = await ReceiveAsync(MaximumMessageBytes);
                var root = result.RootElement;
                if (Type(result) != "result" || !root.GetProperty("id").TryGetInt32(out var id) || id != 1 ||
                    root.GetProperty("success").ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException();
                var snapshot = HomeAssistantLogParser.Parse(root.GetProperty("result"), token, level, integration, ct);
                ct.ThrowIfCancellationRequested();
                // Nincs lezárási receive-várakozás: egyetlen olvasás, aztán lokális abort/dispose.
                return snapshot;
            }
            finally { socket.Abort(); }

            async Task SendAsync<T>(T message)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(message);
                await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, ct);
            }

            async Task<JsonDocument> ReceiveAsync(int maximum)
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[8_192];
                for (var fragment = 0; fragment < MaximumFragments; fragment++)
                {
                    ct.ThrowIfCancellationRequested();
                    var received = await socket.ReceiveAsync(chunk.AsMemory(), ct);
                    bytes += received.Count;
                    if (received.MessageType != WebSocketMessageType.Text || buffer.Length + received.Count > maximum || bytes > MaximumTotalBytes)
                        throw new InvalidOperationException();
                    buffer.Write(chunk, 0, received.Count);
                    if (!received.EndOfMessage) continue;
                    // A JSON parser és a szigorú UTF-8 dekóder egyaránt a teljes kereten dolgozik.
                    var text = new UTF8Encoding(false, true).GetString(buffer.ToArray());
                    var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
                    try
                    {
                        ValidateObjects(document.RootElement);
                        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
                        return document;
                    }
                    catch { document.Dispose(); throw; }
                }
                throw new InvalidOperationException();
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("A Home Assistant naplólekérés megszakítva.", cancellationToken);
        }
        catch (Exception)
        {
            // Nyers provider/WS/JSON/regex adat és belső kivétel nem lépheti át ezt a határt.
            throw new HomeAssistantException("A Home Assistant naplólekérés sikertelen.");
        }
    }

    private static string? Type(JsonDocument message) => message.RootElement.GetProperty("type").GetString();

    private static void ValidateObjects(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidOperationException();
                ValidateObjects(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateObjects(item);
    }
}
