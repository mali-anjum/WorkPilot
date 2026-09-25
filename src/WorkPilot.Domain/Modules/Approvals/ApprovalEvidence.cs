namespace WorkPilot.Domain.Modules.Approvals;

/// <summary>What an approval gated action acts on, for the approver (spec 0007, AC-3).</summary>
/// <param name="Type">The kind of entity, e.g. <c>Profile</c>, <c>Job</c>, <c>OutreachContact</c>.</param>
/// <param name="Id">The entity's id, when it has one.</param>
/// <param name="Label">A human readable name for it.</param>
public sealed record ApprovalTarget(string Type, Guid? Id, string Label);

/// <summary>One immutable document version the action involves (a <c>ResumeVersion</c> or <c>CoverLetterVersion</c>).</summary>
/// <param name="Kind">Which kind of document, e.g. <c>Resume</c> or <c>CoverLetter</c>.</param>
/// <param name="VersionId">The immutable version row's id.</param>
/// <param name="Name">The document's name.</param>
/// <param name="VersionNumber">The version's number within its document.</param>
/// <param name="CreatedAt">When the version was created.</param>
public sealed record DocumentVersionEvidence(string Kind, Guid VersionId, string Name, int VersionNumber, DateTimeOffset CreatedAt);

/// <summary>
/// The evidence snapshot frozen onto an <see cref="Approval"/> when its run
/// suspends (spec 0007, AC-3). Written once, never updated: the approver
/// decides on exactly what will run.
/// </summary>
/// <param name="Summary">A plain sentence saying what the action will do.</param>
/// <param name="Target">What it acts on, when it has a single target.</param>
/// <param name="Documents">The document versions it involves; empty when none.</param>
public sealed record ApprovalEvidence(string Summary, ApprovalTarget? Target, IReadOnlyList<DocumentVersionEvidence> Documents);
