using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentGate.Integrations.HomeAssistant.LogRead;
using AgentGate.Secrets;

namespace AgentGate.Integrations.HomeAssistant.Registry;

// Csak a kliensben rögzített registry parancsok hívják; nem MCP-proxy.
internal static class RegistrySocket
{
    public static async Task<JsonElement> CallAsync(IHomeAssistantLogSocketFactory sockets, Uri origin,
        ISecretProvider secrets, Guid secretId, TimeSpan timeout, IReadOnlyDictionary<string, object?> command,
        JsonElement? intent, bool lossless, CancellationToken cancellationToken, Func<CancellationToken, Task>? beforeSend = null)
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
                !Regex.IsMatch(token, @"\A[A-Za-z0-9\-._~+/]+=*\z", RegexOptions.CultureInvariant)) throw new InvalidOperationException();
            // A szándékot még a tartós előállapot-audit ELŐTT ellenőrizzük az ismert titokkal.
            if (intent is { } input) CheckToken(input, token);
            var target = new UriBuilder(origin) { Scheme = origin.Scheme == "https" ? "wss" : "ws", Path = "/api/websocket", Query = "", Fragment = "" }.Uri;
            using var socket = await sockets.ConnectAsync(target, ct);
            var total = 0;
            try
            {
                using var required = await Receive(4096);
                if (required.RootElement.GetProperty("type").GetString() != "auth_required") throw new InvalidOperationException();
                await Send(new { type = "auth", access_token = token });
                using var auth = await Receive(4096);
                if (auth.RootElement.GetProperty("type").GetString() == "auth_invalid")
                {
                    secrets.Invalidate(secretId, lease);
                    throw new InvalidOperationException();
                }
                if (auth.RootElement.GetProperty("type").GetString() != "auth_ok") throw new InvalidOperationException();
                if (beforeSend is not null) await beforeSend(ct);
                ct.ThrowIfCancellationRequested();
                await Send(command);
                using var response = await Receive(1_048_576);
                var root = response.RootElement;
                if (root.GetProperty("type").GetString() != "result" || root.GetProperty("id").GetInt32() != 1 || root.GetProperty("success").ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException();
                var result = root.GetProperty("result");
                if (lossless) CheckToken(result, token);
                return lossless ? result.Clone() : HomeAssistantJsonSanitizer.Sanitize(result, token);
            }
            finally { socket.Abort(); }

            async Task Send<T>(T message) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message).AsMemory(), WebSocketMessageType.Text, true, ct);
            async Task<JsonDocument> Receive(int maximum)
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                for (var fragment = 0; fragment < 256; fragment++)
                {
                    var received = await socket.ReceiveAsync(chunk.AsMemory(), ct);
                    total += received.Count;
                    if (received.MessageType != WebSocketMessageType.Text || buffer.Length + received.Count > maximum || total > 1_056_768) throw new InvalidOperationException();
                    buffer.Write(chunk, 0, received.Count);
                    if (!received.EndOfMessage) continue;
                    var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(buffer.ToArray()), new JsonDocumentOptions { MaxDepth = 16 });
                    try { ValidateUnique(document.RootElement); return document; }
                    catch { document.Dispose(); throw; }
                }
                throw new InvalidOperationException();
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) { throw new HomeAssistantException("A Home Assistant nyilvántartási művelet sikertelen."); }
    }

    internal static void ValidateUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new InvalidOperationException();
                ValidateUnique(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) ValidateUnique(item);
    }

    private static void CheckToken(JsonElement value, string token)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString()!.Contains(token, StringComparison.Ordinal)) throw new InvalidOperationException();
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var field in value.EnumerateObject())
            {
                if (field.Name.Contains(token, StringComparison.Ordinal)) throw new InvalidOperationException();
                CheckToken(field.Value, token);
            }
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckToken(item, token);
    }
}
