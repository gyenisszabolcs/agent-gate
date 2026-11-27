using System.Globalization;

namespace AgentGate.Approval;

// Csak a gate által számított számlálók és zárt hibakategóriák; nincs provider-hibaszöveg.
public sealed record ApprovalBatchProgress(int Total, int Completed, int? StoppedAt, string Reason)
{
    public int Failed => StoppedAt is null ? 0 : 1;
    public int NotStarted => Total - Completed - Failed;
    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"Elkészült: {Completed}/{Total}; megállt: {(StoppedAt is { } index ? index.ToString(CultureInfo.InvariantCulture) : "–")}; sikertelen/bizonytalan: {Failed}; el nem indult: {NotStarted}. {Reason}");
}

public sealed record ApprovalBatchItem(int Position, string Target, string Name,
    IReadOnlyDictionary<string, string?> Parameters, ApprovalPresentation Display, string State = "NotStarted");
