using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Tests;

file sealed class TestEntity : Entity;

file sealed class TestSoftDeletableEntity : SoftDeletableEntity;

public class EntityBaseTests
{
    [Fact]
    public void Entity_AssignsAUniqueIdOnConstruction()
    {
        var first = new TestEntity();
        var second = new TestEntity();

        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Entity_IdsAreTimeOrdered()
    {
        // uuid v7 (spec 0002): later constructed entities sort after earlier
        // ones, which plain random GUIDs (v4) would not guarantee.
        var first = new TestEntity();
        var second = new TestEntity();

        Assert.True(second.Id.CompareTo(first.Id) > 0);
    }

    [Fact]
    public void SoftDeletableEntity_StartsNotDeleted()
    {
        var entity = new TestSoftDeletableEntity();

        Assert.False(entity.IsDeleted);
        Assert.Null(entity.DeletedAt);
    }

    [Fact]
    public void SoftDelete_SetsIsDeletedAndDeletedAt()
    {
        var entity = new TestSoftDeletableEntity();
        var occurredAt = DateTimeOffset.UtcNow;

        entity.SoftDelete(occurredAt);

        Assert.True(entity.IsDeleted);
        Assert.Equal(occurredAt, entity.DeletedAt);
    }
}
