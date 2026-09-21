using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Tasks;

/// <summary>Named <c>TaskItem</c>, not <c>Task</c>, to avoid colliding with <see cref="System.Threading.Tasks.Task"/>.</summary>
public class TaskItem : SoftDeletableEntity
{
    public required Guid ProfileId { get; init; }
    public required string Title { get; set; }
    public DateOnly? DueDate { get; set; }
    public required string Status { get; set; }
    public Guid? LinkedApplicationId { get; set; }
    public Guid? LinkedOutreachMessageId { get; set; }
}
