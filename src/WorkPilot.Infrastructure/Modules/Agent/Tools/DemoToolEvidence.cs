using Microsoft.EntityFrameworkCore;
using WorkPilot.Domain.Modules.Approvals;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Agent.Tools;

/// <summary>
/// Shared evidence lookups for the two inert demo tools (spec 0007): the
/// calling profile as the target, and the profile's newest resume and cover
/// letter versions as the documents, when they exist. Real tools describe
/// the exact versions pinned in their own arguments instead.
/// </summary>
internal static class DemoToolEvidence
{
    public static async Task<ApprovalTarget> ProfileTargetAsync(WorkPilotDbContext db, Guid profileId, CancellationToken cancellationToken)
    {
        var name = await db.Profiles.Where(p => p.Id == profileId).Select(p => p.Name).FirstOrDefaultAsync(cancellationToken);
        return new ApprovalTarget("Profile", profileId, name ?? "Unknown profile");
    }

    public static async Task<IReadOnlyList<DocumentVersionEvidence>> LatestDocumentsAsync(WorkPilotDbContext db, Guid profileId, CancellationToken cancellationToken)
    {
        var documents = new List<DocumentVersionEvidence>();

        // The active resume's newest version, else the newest version of any resume.
        var resume = await (
                from r in db.Resumes
                join v in db.ResumeVersions on r.Id equals v.ResumeId
                where r.ProfileId == profileId
                orderby r.IsActive descending, v.CreatedAt descending, v.VersionNumber descending
                select new { v.Id, r.Name, v.VersionNumber, v.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (resume is not null)
        {
            documents.Add(new DocumentVersionEvidence("Resume", resume.Id, resume.Name, resume.VersionNumber, resume.CreatedAt));
        }

        var coverLetter = await (
                from c in db.CoverLetters
                join v in db.CoverLetterVersions on c.Id equals v.CoverLetterId
                where c.ProfileId == profileId
                orderby v.CreatedAt descending, v.VersionNumber descending
                select new { v.Id, c.Name, v.VersionNumber, v.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (coverLetter is not null)
        {
            documents.Add(new DocumentVersionEvidence("CoverLetter", coverLetter.Id, coverLetter.Name, coverLetter.VersionNumber, coverLetter.CreatedAt));
        }

        return documents;
    }
}
