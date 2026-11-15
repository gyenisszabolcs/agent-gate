namespace AgentGate.Secrets;

// A szolgáltatói határ tesztben helyettesíthető; a null hiányzó titkot jelent.
public interface IBitwardenSecretClient
{
    Task<string?> GetSecretAsync(Guid secretId, string accessToken, CancellationToken cancellationToken);
}
