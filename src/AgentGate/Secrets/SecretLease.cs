namespace AgentGate.Secrets;

// Az objektumazonosság a lekérési generációt jelöli, nem a titok szöveges egyezése.
public sealed class SecretLease(string value)
{
    public string Value { get; } = value;
    public override string ToString() => "Titokreferencia";
}
