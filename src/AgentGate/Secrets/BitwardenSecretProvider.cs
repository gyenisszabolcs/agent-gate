namespace AgentGate.Secrets;

public sealed class BitwardenSecretProvider(
    IBitwardenSecretClient? client = null,
    Func<string, string?>? environmentReader = null,
    string credentialFileName = "bitwarden-access-token",
    bool credentialOnly = false) : ISecretProvider
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
            var directory = _environmentReader("CREDENTIALS_DIRECTORY");
            ThrowIfCanceled(cancellationToken);
            if (directory is not null &&
                (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory) ||
                 string.IsNullOrEmpty(credentialFileName) || credentialFileName is "." or ".." ||
                 credentialFileName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-')))
            {
                throw new SecretProviderException(SecretProviderError.ProviderFailure);
            }

            if (directory is not null)
            {
                // A systemd által védett, csak olvasható gyökér és ősei megbízhatók;
                // írható könyvtárban ez az ellenőrzés nem ad versenyhelyzet elleni védelmet.
                var rootAttributes = File.GetAttributes(Path.TrimEndingDirectorySeparator(directory));
                var credentialAttributes = File.GetAttributes(Path.Combine(directory, credentialFileName));
                if ((rootAttributes & FileAttributes.ReparsePoint) != 0 ||
                    (credentialAttributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                {
                    throw new SecretProviderException(SecretProviderError.ProviderFailure);
                }
            }

            accessToken = directory is null
                ? (credentialOnly ? null : _environmentReader(AccessTokenEnvironmentVariable))
                : await File.ReadAllTextAsync(Path.Combine(directory, credentialFileName), cancellationToken).ConfigureAwait(false);
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
        accessToken = accessToken?.TrimEnd();
        if (string.IsNullOrEmpty(accessToken))
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
