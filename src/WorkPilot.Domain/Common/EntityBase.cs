namespace WorkPilot.Domain.Common;

/// <summary>Base for every entity: a time ordered GUID primary key (uuid v7, spec 0002).</summary>
public abstract class Entity
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
}

/// <summary>Base for entities that are hidden, never physically removed, so referencing history survives (spec 0002).</summary>
public abstract class SoftDeletableEntity : Entity, ISoftDeletable
{
    public bool IsDeleted { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    public void SoftDelete(DateTimeOffset occurredAtUtc)
    {
        IsDeleted = true;
        DeletedAt = occurredAtUtc;
    }
}
