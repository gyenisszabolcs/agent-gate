using AgentGate.Integrations.HomeAssistant.Control;
using AgentGate.Tools;

namespace AgentGate.Approval.HomeAssistant;

public sealed partial class ApprovalPushFormatter
{
    public string Outcome(ApprovalNotice request, ApprovalState state, string token, string? result = null)
    {
        var status = state switch
        {
            ApprovalState.Executed => "Kész",
            ApprovalState.Rejected => "Elutasítva",
            ApprovalState.Expired => "Lejárt",
            ApprovalState.Failed => "Sikertelen vagy bizonytalan",
            _ => throw new InvalidOperationException("Az eredményértesítés nem küldhető.")
        };
        string action;
        string target;
        try
        {
            (action, target) = Content(request, token);
        }
        catch (Exception error) when (error is GateToolException or InvalidOperationException)
        {
            // Például egy időközben forgatott token megjelenhetett a korábbi emberi névben.
            // A státuszt ilyenkor is közöljük, nyers paraméterhez vagy eredményhez nem nyúlunk.
            action = "művelet (részletek nem jeleníthetők meg biztonságosan)";
            target = "Kiválasztott eszköz";
        }
        var warning = state == ApprovalState.Failed ? ". Nem jelent visszagörgetést" : "";

        // A változásokra a jóváhagyás ugyanazon veszteségmentes, 160 karakteres korlátja érvényes.
        // Nincs szabad szövegű HA-hiba, technikai azonosító vagy hívói eredményszöveg.
        // A második sor UTF-16 kerete a legfeljebb 640 kódegységes RegistryLine-t is vágás nélkül őrzi.
        var message = SafeLine($"{status}: {action}{warning}", 128, token) + "\n" + SafeLine($"Kért változás: {target}", 656, token);
        if (state == ApprovalState.Failed && request.Tool == "ha_sensitive_control")
        {
            foreach (var failure in Enum.GetValues<TtsFailure>())
            {
                // Kizárólag a végrehajtó zárt hibakategóriáinak TELJES egyezése engedett.
                // A kimenet a helyi konstansból készül, nem a kapott eredményszövegből.
                var known = new TtsExecutionException(failure).SafeMessage;
                if (!string.Equals(result, known, StringComparison.Ordinal)) continue;
                var category = known[..known.IndexOf(" A hangerőállítás", StringComparison.Ordinal)];
                return message + "\n" + SafeLine(category, 128, token);
            }
        }
        return message;
    }
}
