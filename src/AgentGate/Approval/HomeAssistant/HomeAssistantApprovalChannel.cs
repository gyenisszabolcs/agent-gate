using System.Net.WebSockets;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AgentGate.Secrets;
using Microsoft.Extensions.Options;

namespace AgentGate.Approval.HomeAssistant;

public sealed class HomeAssistantApprovalChannel(
    IOptions<HomeAssistantApprovalOptions> configured,
    ISecretProvider secrets,
    IApprovalWebSocketFactory sockets,
    IApprovalHttpHandlerFactory httpHandlers,
    TimeProvider clock) : BackgroundService, IApprovalChannel
{
    private readonly HomeAssistantApprovalOptions options = configured.Value.IsValid()
        ? configured.Value : throw new OptionsValidationException(Options.DefaultName, typeof(HomeAssistantApprovalOptions), [HomeAssistantApprovalOptions.SafeMessage]);
    private readonly Channel<ApprovalDecision> decisions = Channel.CreateBounded<ApprovalDecision>(256);
    private volatile bool available;
    private readonly HttpClient http = new(httpHandlers.CreateHandler())
    {
        BaseAddress = new Uri(configured.Value.BaseUrl!),
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly object sync = new();
    private readonly Dictionary<Guid, NotificationGrant> notifications = new();
    private readonly ApprovalChannelWork secretWork = new(clock, TimeSpan.FromSeconds(configured.Value.TimeoutSeconds));
    public bool IsAvailable => available;
    public string Name => "home-assistant";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoffSeconds = 1;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var wasAvailable = await RunConnectionAsync(stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;
                if (wasAvailable) backoffSeconds = 1;
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), clock, stoppingToken);
                backoffSeconds = Math.Min(backoffSeconds * 2, 30);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<bool> RunConnectionAsync(CancellationToken stoppingToken)
    {
        var wasAvailable = false;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            handshake.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var uri = new UriBuilder(options.BaseUrl!)
            {
                Scheme = new Uri(options.BaseUrl!).Scheme == "https" ? "wss" : "ws",
                Path = "/api/websocket"
            }.Uri;
            using var socket = await sockets.ConnectAsync(uri, handshake.Token);
            using (var required = await ReceiveAsync(socket, handshake.Token))
                RequireType(required.RootElement, "auth_required");
            var token = await ReadTokenAsync(handshake.Token);
            await SendAsync(socket, new { type = "auth", access_token = token }, handshake.Token);
            using (var authenticated = await ReceiveAsync(socket, handshake.Token))
                RequireType(authenticated.RootElement, "auth_ok");
            await SendAsync(socket, new { id = 1, type = "subscribe_events", event_type = "mobile_app_notification_action" }, handshake.Token);
            using (var subscribed = await ReceiveAsync(socket, handshake.Token))
            {
                RequireType(subscribed.RootElement, "result");
                if (!subscribed.RootElement.TryGetProperty("id", out var id) || !id.TryGetInt32(out var value) || value != 1 ||
                    !subscribed.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException();
            }
            available = true;
            wasAvailable = true;
            handshake.CancelAfter(Timeout.InfiniteTimeSpan);
            while (!stoppingToken.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                idle.CancelAfter(TimeSpan.FromSeconds(options.MaxIdleSeconds));
                using var message = await ReceiveAsync(socket, idle.Token);
                ProcessEvent(message.RootElement);
            }
        }
        catch (Exception)
        {
            // Nincs nyers token, keret, HA-hiba vagy kivétel a diagnosztikában.
        }
        finally { available = false; }
        return wasAvailable;
    }

    private void ProcessEvent(JsonElement message)
    {
        if (!available || !TryString(message, "type", out var type) || type != "event" ||
            !message.TryGetProperty("id", out var subscription) || subscription.ValueKind != JsonValueKind.Number || !subscription.TryGetInt32(out var subscriptionId) || subscriptionId != 1 ||
            !message.TryGetProperty("event", out var body) || !TryString(body, "event_type", out var eventType) || eventType != "mobile_app_notification_action" ||
            !body.TryGetProperty("context", out var context) || !TryString(context, "user_id", out var user) || user != options.ExpectedUserId ||
            !body.TryGetProperty("data", out var data) || !TryString(data, "action", out var action) || action.Length != 64 ||
            action.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            return;
        if (options.ExpectedDeviceId is { } expected && data.TryGetProperty("device_id", out var device) &&
            (device.ValueKind != JsonValueKind.String || !HomeAssistantApprovalOptions.IsIdentifier(device.GetString()) || device.GetString() != expected))
            return;
        lock (sync)
        {
            var grant = notifications.Values.FirstOrDefault(item => item.ApproveCode == action || item.RejectCode == action);
            if (grant is null || grant.Decided || clock.GetUtcNow() >= grant.Notice.ExpiresAt)
                return;
            var choice = grant.ApproveCode == action ? ApprovalChoice.Approve : ApprovalChoice.Reject;
            if (decisions.Writer.TryWrite(new ApprovalDecision(grant.Notice.Id, choice, options.ExpectedUserId!, clock.GetUtcNow())))
                grant.Decided = true;
        }
    }

    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String)
            return false;
        value = item.GetString()!;
        return true;
    }

    private static void RequireType(JsonElement message, string type)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var actual) || actual.ValueKind != JsonValueKind.String || actual.GetString() != type)
            throw new InvalidOperationException();
    }

    private static async Task SendAsync(WebSocket socket, object message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(chunk.AsMemory(), cancellationToken);
            if (result.MessageType != WebSocketMessageType.Text || buffer.Length + result.Count > 65536)
                throw new InvalidOperationException();
            await buffer.WriteAsync(chunk.AsMemory(0, result.Count), cancellationToken);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }

    public async Task SendRequestAsync(ApprovalNotice request, CancellationToken cancellationToken)
    {
        if (!available || request.Id == Guid.Empty || request.ExpiresAt.Offset != TimeSpan.Zero || clock.GetUtcNow() >= request.ExpiresAt ||
            request.Risk is not (Policy.RiskLevel.SensitiveControl or Policy.RiskLevel.Configuration or Policy.RiskLevel.System) ||
            request.Tool.Length > 128 || !Regex.IsMatch(request.Tool, @"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("A jóváhagyási kérés nem küldhető.");
        var grant = new NotificationGrant(request, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        lock (sync)
        {
            // A lejárt kódot a döntésellenőrzés tiltja; az eredmény metaadata A értesítéséig megmarad.
            if (notifications.Count >= 1024 || !notifications.TryAdd(request.Id, grant))
                throw new InvalidOperationException("A jóváhagyási kérés nem küldhető.");
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var token = await ReadTokenAsync(deadline.Token);
            var message = $"{SafeText(request.Caller, 128, token)}\n{request.Tool}\n{request.Risk}\n{SafeText(request.Summary, 512, token)}\nLejárat: {request.ExpiresAt:O}\nAzonosító: {request.Id.ToString("N")[..8]}";
            message = message.Replace(token, "[REDACTED]", StringComparison.Ordinal);
            if (!available) throw new InvalidOperationException();
            await PostAsync(new
            {
                title = "Gate jóváhagyás",
                message,
                data = new { actions = new[] { new { action = grant.ApproveCode, title = "Jóváhagyom" }, new { action = grant.RejectCode, title = "Elutasítom" } } }
            }, token, deadline.Token);
            if (!available) throw new InvalidOperationException();
        }
        catch (Exception)
        {
            lock (sync) notifications.Remove(request.Id);
            throw new InvalidOperationException("A jóváhagyási kérés nem küldhető.");
        }
    }

    private async Task<string> ReadTokenAsync(CancellationToken cancellationToken)
    {
        var valueLock = new object();
        string? value = null;
        var accepting = true;
        bool succeeded;
        try
        {
            succeeded = await secretWork.SendAsync(async token =>
            {
                var found = await secrets.GetSecretAsync(Guid.Parse(options.TokenSecretId!), token);
                lock (valueLock)
                    if (accepting && !token.IsCancellationRequested) value = found;
            }, cancellationToken);
        }
        finally
        {
            lock (valueLock) accepting = false;
        }
        string? result;
        lock (valueLock) { result = value; value = null; }
        if (!succeeded || result is not { Length: >= 1 and <= 16384 } || !Regex.IsMatch(result, @"\A[A-Za-z0-9\-._~+/]+=*\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException();
        return result;
    }

    private async Task PostAsync(object payload, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/services/notify/" + options.NotifyService!["notify.".Length..]);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + read > 65536) throw new InvalidOperationException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        using var acknowledgement = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        if (acknowledgement.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException();
    }

    private static string SafeText(string value, int limit, string token)
    {
        var safe = SecretRedactor.RedactText(value.Replace(token, "[REDACTED]", StringComparison.Ordinal))!;
        return safe.Length <= limit ? safe : safe[..limit];
    }

    private sealed record NotificationGrant(ApprovalNotice Notice, string ApproveCode, string RejectCode)
    {
        public bool Decided { get; set; }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await secretWork.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        secretWork.Dispose();
        http.Dispose();
        base.Dispose();
    }

    public IAsyncEnumerable<ApprovalDecision> ReadDecisionsAsync(CancellationToken cancellationToken) => decisions.Reader.ReadAllAsync(cancellationToken);
    public async Task SendOutcomeAsync(ApprovalOutcome outcome, CancellationToken cancellationToken)
    {
        NotificationGrant grant;
        lock (sync)
        {
            if (!notifications.Remove(outcome.Id, out grant!) || outcome.State == ApprovalState.Pending || !Enum.IsDefined(outcome.State))
                throw new InvalidOperationException("Az eredményértesítés nem küldhető.");
        }
        try
        {
            if (!available) throw new InvalidOperationException();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var token = await ReadTokenAsync(deadline.Token);
            var state = outcome.State switch
            {
                ApprovalState.Executed => "jóváhagyva és végrehajtva",
                ApprovalState.Rejected => "elutasítva",
                ApprovalState.Expired => "lejárt",
                _ => "sikertelen; ez nem jelenti a már végrehajtott művelet visszagörgetését"
            };
            var message = $"{SafeText(grant.Notice.Caller, 128, token)}\n{grant.Notice.Tool}\n{state}\n{SafeText(outcome.Result ?? "", 512, token)}\nAzonosító: {outcome.Id.ToString("N")[..8]}";
            await PostAsync(new { title = "Gate eredmény", message = message.Replace(token, "[REDACTED]", StringComparison.Ordinal) }, token, deadline.Token);
        }
        catch (Exception)
        {
            // A döntés és a gombok már végleg lezártak; értesítést sem próbálunk automatikusan újra.
            throw new InvalidOperationException("Az eredményértesítés nem küldhető.");
        }
    }
}
