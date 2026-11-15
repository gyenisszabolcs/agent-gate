namespace AgentGate.Tools;

/// <summary>Kifejezetten kiadhatónak szánt eszközhiba; az üzenet ezen az úton is titokszűrt.</summary>
public sealed class GateToolException(string message) : Exception(message);
