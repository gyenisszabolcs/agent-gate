namespace AgentGate.Secrets;

public interface ISecretProvider
{
    Task<string> GetSecretAsync(Guid secretId, CancellationToken cancellationToken = default);

    Task<string> GetFreshSecretAsync(Guid secretId, CancellationToken cancellationToken = default) =>
        GetSecretAsync(secretId, cancellationToken);

    async Task<SecretLease> GetSecretLeaseAsync(Guid secretId, CancellationToken cancellationToken = default) =>
        new(await GetSecretAsync(secretId, cancellationToken));

    // Csak a ténylegesen elutasított generációt vonjuk vissza; nincs hívói MCP-út.
    void Invalidate(Guid secretId, SecretLease rejectedLease) { }
}
