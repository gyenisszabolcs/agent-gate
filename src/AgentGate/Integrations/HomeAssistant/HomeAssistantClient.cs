using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentGate.Secrets;
using Microsoft.Extensions.Options;

namespace AgentGate.Integrations.HomeAssistant;

public sealed class HomeAssistantClient : IDisposable
{
    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    private readonly ISecretProvider secrets;
    private readonly Guid secretId;
    private readonly HashSet<string> domains;
    private const int MaximumResponseBytes = 1024 * 1024;

    public HomeAssistantClient(IOptions<HomeAssistantOptions> options, ISecretProvider secrets)
        : this(options, secrets, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }) { }

    // A megadott handler megbízható infrastruktúra; tesztben kizárólag hamis HTTP-kezelő.
    public HomeAssistantClient(IOptions<HomeAssistantOptions> options, ISecretProvider secrets, HttpMessageHandler handler)
    {
        if (new HomeAssistantOptionsValidator().Validate(null, options.Value).Failed)
            throw new OptionsValidationException(Options.DefaultName, typeof(HomeAssistantOptions), [HomeAssistantOptionsValidator.SafeMessage]);
        this.secrets = secrets;
        domains = new HashSet<string>(options.Value.DomainAllowlist ?? [], StringComparer.Ordinal);
        secretId = Guid.Parse(options.Value.TokenSecretId!);
        timeout = options.Value.Timeout;
        // Egyetlen határidő fedi a titoklekérést, a HTTP-hívást és a választest olvasását.
        http = new HttpClient(handler) { BaseAddress = new Uri(options.Value.BaseUrl!), Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public async Task<HomeAssistantConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        var json = await ReadAsync("/api/config", cancellationToken);
        if (json.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        return new HomeAssistantConfig(json);
    }

    public async Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(CancellationToken cancellationToken = default)
    {
        var json = await ReadAsync("/api/states", cancellationToken);
        if (json.ValueKind != JsonValueKind.Array) throw InvalidResponse();
        var states = new List<HomeAssistantState>();
        foreach (var element in json.EnumerateArray())
        {
            if (!TryString(element, "entity_id", out var id)) throw InvalidResponse();
            if (IsAllowed(id)) states.Add(ToState(element));
        }
        return states.AsReadOnly();
    }

    public async Task<HomeAssistantState> GetStateAsync(string entityId, CancellationToken cancellationToken = default)
    {
        if (!IsAllowed(entityId))
            throw new HomeAssistantException("A Home Assistant entitás nem engedélyezett.");
        var state = ToState(await ReadAsync("/api/states/" + entityId, cancellationToken));
        if (state.EntityId != entityId) throw InvalidResponse();
        return state;
    }

    public bool IsDomainAllowed(string? domain) => domain is { Length: > 0 and <= 255 } &&
        Regex.IsMatch(domain, @"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant) && domains.Contains(domain);

    public bool IsEntityAllowed(string? entityId) => IsAllowed(entityId);

    private bool IsAllowed(string? entityId) => entityId is not null && entityId.Length <= 255 &&
        Regex.IsMatch(entityId, @"\A[a-z0-9_]+\.[a-z0-9_]+\z", RegexOptions.CultureInvariant) &&
        domains.Contains(entityId[..entityId.IndexOf('.')]);

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString()!;
        return true;
    }

    private static HomeAssistantState ToState(JsonElement element)
    {
        if (!TryString(element, "entity_id", out var id) || !TryString(element, "state", out var state) ||
            !element.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object)
            throw InvalidResponse();
        var lastChanged = TryString(element, "last_changed", out var timestamp) &&
            Regex.IsMatch(timestamp, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant) &&
            DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _) ? timestamp : null;
        return new HomeAssistantState(id, state, attributes.Clone(), lastChanged);
    }

    private static HomeAssistantException InvalidResponse() => new("A Home Assistant válasz érvénytelen.");

    internal Task<JsonElement> PostServiceAsync(Control.ControlRequest request, Func<CancellationToken, Task> beforeSend, CancellationToken cancellationToken)
    {
        var body = request.Data.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        body.Add("entity_id", JsonSerializer.SerializeToElement(request.EntityId));
        return SendAsync("/api/services/" + request.Domain + "/" + request.Service, cancellationToken,
            JsonSerializer.Serialize(body), beforeSend);
    }

    private Task<JsonElement> ReadAsync(string path, CancellationToken cancellationToken) => SendAsync(path, cancellationToken);

    private async Task<JsonElement> SendAsync(string path, CancellationToken cancellationToken, string? body = null, Func<CancellationToken, Task>? beforeSend = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct = deadline.Token;
        Control.ControlRateLimitException? quotaRejection = null;
        Control.TtsQuietHoursException? quietRejection = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var token = await secrets.GetSecretAsync(secretId, ct);
            if (string.IsNullOrEmpty(token) || token.Length > 16384 ||
                !Regex.IsMatch(token, @"\A[A-Za-z0-9\-._~+/]+=*\z", RegexOptions.CultureInvariant))
                throw new InvalidOperationException();
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
            if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (beforeSend is not null)
            {
                try { await beforeSend(ct); }
                catch (Control.ControlRateLimitException exception)
                {
                    // Csak a helyi előfeltételből érkező, állandó kvótahiba léphet át a transporthatáron.
                    quotaRejection = exception;
                    throw;
                }
                catch (Control.TtsQuietHoursException exception)
                {
                    quietRejection = exception;
                    throw;
                }
            }
            ct.ThrowIfCancellationRequested();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) != 0)
            {
                if (buffer.Length + read > MaximumResponseBytes) throw new InvalidOperationException();
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            }
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            ct.ThrowIfCancellationRequested();
            return HomeAssistantJsonSanitizer.Sanitize(json.RootElement, token);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("A Home Assistant lekérés megszakítva.", cancellationToken);
        }
        catch (Exception) when (deadline.IsCancellationRequested)
        {
            throw new HomeAssistantException("A Home Assistant lekérés időkorlátja lejárt.");
        }
        catch (Control.ControlRateLimitException exception) when (ReferenceEquals(exception, quotaRejection))
        {
            throw;
        }
        catch (Control.TtsQuietHoursException exception) when (ReferenceEquals(exception, quietRejection))
        {
            throw;
        }
        catch (Exception)
        {
            // Szándékosan nincs belső kivétel, választest, fejléc, URL vagy szolgáltatói üzenet.
            throw new HomeAssistantException("A Home Assistant lekérés sikertelen.");
        }
    }

    public void Dispose() => http.Dispose();
}
