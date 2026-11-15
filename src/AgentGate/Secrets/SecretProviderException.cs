namespace AgentGate.Secrets;

public enum SecretProviderError
{
    MissingAccessToken,
    NotFound,
    AuthenticationFailed,
    ProviderFailure,
    UnsafeDiagnostics
}

// Kizárólag rögzített üzenet; szolgáltatói kivétel és adat nem kerülhet bele.
public sealed class SecretProviderException(SecretProviderError error)
    : Exception(error switch
    {
        SecretProviderError.MissingAccessToken => "A Bitwarden gépi token nincs beállítva.",
        SecretProviderError.NotFound => "A kért Bitwarden titok nem található vagy nem hozzáférhető.",
        SecretProviderError.AuthenticationFailed => "A Bitwarden hitelesítés sikertelen.",
        SecretProviderError.UnsafeDiagnostics => "A Bitwarden SDK használatához a RUST_LOG környezeti változó értéke off legyen.",
        _ => "A Bitwarden titok lekérése sikertelen."
    })
{
    public SecretProviderError Error { get; } = error;
}
