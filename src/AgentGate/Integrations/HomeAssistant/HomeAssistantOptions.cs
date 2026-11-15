namespace AgentGate.Integrations.HomeAssistant;

public sealed class HomeAssistantOptions
{
    public string? BaseUrl { get; set; }
    public bool AllowHttp { get; set; } = false;
    // Szövegként kötjük, hogy hibás GUID esetén se a binder visszhangozza a bemenetet.
    public string? TokenSecretId { get; set; }
    public string[]? DomainAllowlist { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
