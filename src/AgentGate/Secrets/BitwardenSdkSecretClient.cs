using Bitwarden.Sdk;

namespace AgentGate.Secrets;

internal sealed class BitwardenSdkSecretClient(Func<string, string?> environmentReader) : IBitwardenSecretClient
{
    public Task<string?> GetSecretAsync(Guid secretId, string accessToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A natív SDK környezeti szűrővel inicializálja a folyamaton belüli naplózót.
        // A hitelesítési kérelem debug naplója titkot tartalmazhat: csak teljes tiltás engedett.
        if (environmentReader("RUST_LOG") != "off")
        {
            throw new SecretProviderException(SecretProviderError.UnsafeDiagnostics);
        }

        // A hivatalos C# SDK szinkron, natív hívásai nem fogadnak megszakítási tokent.
        // Folyamatban lévő hívást nem szakítunk meg erőszakkal; előtte/utána ellenőrzünk.
        return Task.Run<string?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sdk = new BitwardenClient();
            try
            {
                // A null állapotfájl a Rust protokollban None: nincs lemezre mentett munkamenet.
                sdk.Auth.LoginAccessToken(accessToken, stateFile: null!);
            }
            catch (BitwardenAuthException)
            {
                throw new UnauthorizedAccessException("A Bitwarden hitelesítés sikertelen.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var response = sdk.Secrets.GetByIds([secretId]);
            cancellationToken.ThrowIfCancellationRequested();

            // A Value az SDK által visszafejtett érték, nem a REST-válasz ciphertext mezője.
            // Üres találati lista: nem található vagy a gépi fiók számára nem elérhető.
            return response.Data.SingleOrDefault(secret => secret.Id == secretId)?.Value;
        }, cancellationToken);
    }
}
