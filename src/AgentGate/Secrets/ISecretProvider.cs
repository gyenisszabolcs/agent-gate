namespace AgentGate.Secrets;

public interface ISecretProvider
{
    Task<string> GetSecretAsync(Guid secretId, CancellationToken cancellationToken = default);
}
