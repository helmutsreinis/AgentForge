namespace AgentForge.Domain.Primitives;

public readonly record struct InstallationId(Guid Value)
{
    public static InstallationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ActorId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct CorrelationId(string Value)
{
    public override string ToString() => Value;
}
