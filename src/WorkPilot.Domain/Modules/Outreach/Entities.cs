using WorkPilot.Domain.Common;

namespace WorkPilot.Domain.Modules.Outreach;

public class OutreachContact : SoftDeletableEntity
{
    public Guid? ProfessorId { get; init; }
    public required string Email { get; set; }
    public required string Name { get; set; }

    public List<OutreachMessage> Messages { get; init; } = [];
}

public class OutreachMessage : Entity
{
    public required Guid OutreachContactId { get; init; }
    public required string Subject { get; set; }
    public required string Body { get; set; }
    public required string Status { get; set; }

    public EmailThread? Thread { get; init; }
    public List<FollowUp> FollowUps { get; init; } = [];
}

/// <summary>1:1 with the <see cref="OutreachMessage"/> that started it, tracking the Gmail thread.</summary>
public class EmailThread : Entity
{
    public required Guid OutreachMessageId { get; init; }
    public required string GmailThreadId { get; init; }
}

public class FollowUp : Entity
{
    public required Guid OutreachMessageId { get; init; }
    public required DateTimeOffset ScheduledFor { get; set; }
    public required string Status { get; set; }
}
