namespace AgentGate.Secrets;

public sealed class BitwardenSecretProvider(
    IBitwardenSecretClient? client = null,
    Func<string, string?>? environmentReader = null) : ISecretProvider
{
    private readonly Func<string, string?> _environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
    private readonly IBitwardenSecretClient _client = client ?? new BitwardenSdkSecretClient(environmentReader ?? Environment.GetEnvironmentVariable);

    public const string AccessTokenEnvironmentVariable = "AGENT_GATE_BITWARDEN_ACCESS_TOKEN";

    public async Task<string> GetSecretAsync(Guid secretId, CancellationToken cancellationToken = default)
    {
        ThrowIfCanceled(cancellationToken);
        if (secretId == Guid.Empty)
        {
            throw new ArgumentException("A titokazonosító nem lehet üres.", nameof(secretId));
        }

        string? accessToken;
        try
        {
            accessToken = _environmentReader(AccessTokenEnvironmentVariable);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw Canceled(cancellationToken);
        }
        catch (Exception)
        {
            throw new SecretProviderException(SecretProviderError.ProviderFailure);
        }

        ThrowIfCanceled(cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new SecretProviderException(SecretProviderError.MissingAccessToken);
        }

        string? value;
        try
        {
            value = await _client.GetSecretAsync(secretId, accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw Canceled(cancellationToken);
        }
        catch (SecretProviderException error)
        {
            // A helyettesített határ kivételobjektuma sem juthat tovább.
            throw new SecretProviderException(error.Error);
        }
        catch (UnauthorizedAccessException)
        {
            throw new SecretProviderException(SecretProviderError.AuthenticationFailed);
        }
        catch (Exception)
        {
            throw new SecretProviderException(SecretProviderError.ProviderFailure);
        }

        ThrowIfCanceled(cancellationToken);
        return value ?? throw new SecretProviderException(SecretProviderError.NotFound);
    }

    private static void ThrowIfCanceled(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw Canceled(cancellationToken);
        }
    }

    private static OperationCanceledException Canceled(CancellationToken cancellationToken) =>
        new("A Bitwarden titok lekérése megszakítva.", cancellationToken);
}
