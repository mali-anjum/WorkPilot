namespace WorkPilot.Application.Modules.Identity;

/// <summary>
/// Resolves the <c>Profile</c> row (`app.profiles`, 1:1 with GoTrue's `auth.users`)
/// for a signed in founder, creating it on their very first sign in. Per spec
/// 0004's Value sourcing table: the sign in cookie's <c>profileId</c> claim comes
/// from this lookup, keyed by the GoTrue user id GoTrue itself just confirmed.
/// </summary>
public interface IProfileProvisioningService
{
    Task<Guid> GetOrCreateProfileIdAsync(Guid authUserId, string email, CancellationToken cancellationToken);
}
