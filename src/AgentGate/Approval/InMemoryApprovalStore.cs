namespace AgentGate.Approval;

/// <summary>
/// Példányonként elkülönített, egyszer használható engedélyeket tárol; újraindításkor minden állapot elvész.
/// </summary>
public sealed class InMemoryApprovalStore(TimeProvider timeProvider)
{
    private readonly TimeProvider clock = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider), "Az időforrás megadása kötelező.");

    private readonly Dictionary<Guid, ApprovalRequest> requests = new();
    private readonly object sync = new();

    /// <summary>
    /// Az ellenőrzés és a sikeres engedély eltávolítása egyetlen zárolt művelet.
    /// Az érvénytelen hash kivételt vált ki; az eltérő, de helyes formátumú hash nem fogyaszt engedélyt.
    /// </summary>
    public bool TryConsume(Guid id, string operationHash)
    {
        ValidateOperationHash(operationHash);
        lock (sync)
        {
            if (!requests.TryGetValue(id, out var request))
            {
                return false;
            }

            if (clock.GetUtcNow() >= request.ExpiresAt)
            {
                requests.Remove(id);
                return false;
            }

            if (!string.Equals(request.OperationHash, operationHash, StringComparison.Ordinal))
            {
                return false;
            }

            return requests.Remove(id);
        }
    }

    /// <summary>
    /// Megbízható jóváhagyó számára létrehoz egy már engedélyezett, jövőbeli UTC lejáratú bejegyzést.
    /// Nem jelent emberi jóváhagyási folyamatot, és nem tehető közvetlenül elérhetővé a hívó agentnek.
    /// </summary>
    public ApprovalRequest Create(string operationHash, DateTimeOffset expiresAt)
    {
        ValidateOperationHash(operationHash);
        lock (sync)
        {
            if (expiresAt.Offset != TimeSpan.Zero || expiresAt <= clock.GetUtcNow())
            {
                throw new ArgumentException("A lejárat jövőbeli, nulla eltolású UTC időpont legyen.", nameof(expiresAt));
            }

            var request = new ApprovalRequest(Guid.NewGuid(), operationHash, expiresAt);
            requests.Add(request.Id, request);
            return request;
        }
    }

    private static void ValidateOperationHash(string operationHash)
    {
        if (operationHash is null || operationHash.Length != 64 ||
            operationHash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A művelethash pontosan 64 kisbetűs ASCII hexadecimális karakterből álló SHA-256 lenyomat legyen.", nameof(operationHash));
        }
    }
}
