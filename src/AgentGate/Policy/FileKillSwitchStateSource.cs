using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AgentGate.Policy;

/// <summary>A friss kapcsolóállapot megszakítható forrása.</summary>
public interface IKillSwitchStateSource
{
    ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken);
}

/// <summary>A gate csak olvassa ezt a fájlt; élesben root kezeli.</summary>
public sealed class KillSwitchFileOptions
{
    public string Path { get; set; } = "/etc/agentgate/kill-switch.json";
}

/// <summary>Nem tárol utolsó jó állapotot; minden ellenőrzés új fájlolvasás.</summary>
public sealed class FileKillSwitchStateSource(
    IOptions<KillSwitchFileOptions> options,
    Func<string, CancellationToken, Task<byte[]>>? readFile = null) : IKillSwitchStateSource
{
    public async ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var json = await (readFile ?? File.ReadAllBytesAsync)(options.Value.Path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte> utf8 = json;
            if (json.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            {
                utf8 = utf8[3..];
            }
            using var document = JsonDocument.Parse(utf8);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }
            var properties = document.RootElement.EnumerateObject().ToArray();
            return properties.Length != 1 || properties[0].Name != "Enabled" ||
                properties[0].Value.ValueKind != JsonValueKind.False;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A hiányzó/hibás/olvashatatlan forrás nem tarthatja nyitva a kaput.
            return true;
        }
    }
}
