using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Identity;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Identity;

/// <inheritdoc cref="IProfileProvisioningService" />
public sealed class ProfileProvisioningService(WorkPilotDbContext db) : IProfileProvisioningService
{
    public async Task<Guid> GetOrCreateProfileIdAsync(Guid authUserId, string email, CancellationToken cancellationToken)
    {
        var existingId = await db.Profiles
            .Where(p => p.AuthUserId == authUserId)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingId is { } id)
        {
            return id;
        }

        // First ever sign in for this GoTrue account: create the Profile row
        // spec 0002 already models as its 1:1 counterpart. The spec names no
        // source for an initial display name, so the email's local part is
        // used as a placeholder; the founder can rename it from /settings once
        // that screen exists.
        var profile = new Domain.Modules.Profile.Profile
        {
            AuthUserId = authUserId,
            Name = email.Split('@')[0],
        };
        db.Profiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        return profile.Id;
    }
}
