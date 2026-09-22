namespace WorkPilot.Application.Modules.Identity;

/// <summary>
/// The single, server-side gateway to GoTrue (Supabase's auth service). Per
/// docs/specs/0004-auth-app-shell.md, this is the only thing that ever talks
/// to GoTrue, and it never keeps a GoTrue access or refresh token past the
/// single request that used it: a successful sign in or reset yields only
/// the founder's identity, which the caller then tracks with its own plain
/// session cookie.
/// </summary>
public interface IAuthService
{
    /// <summary>Checks the email and password against GoTrue. Never throws on invalid credentials.</summary>
    Task<SignInResult> SignInAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>
    /// Asks GoTrue to send a password recovery email. Always succeeds from the
    /// caller's point of view (AC-5): GoTrue itself decides silently whether the
    /// email exists or whether its own rate limit applies, so there is nothing
    /// here for the app to reveal either way.
    /// </summary>
    Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken);

    /// <summary>
    /// Completes a password reset using the <c>token_hash</c> from GoTrue's
    /// recovery email link (verified server side against GoTrue's own
    /// <c>/verify</c> endpoint), then sets the new password.
    /// </summary>
    Task<PasswordResetResult> ResetPasswordAsync(string tokenHash, string newPassword, CancellationToken cancellationToken);
}

/// <summary>The founder's identity as GoTrue confirmed it; never a token.</summary>
public sealed record SignInResult(bool Succeeded, Guid? AuthUserId, string? Email);

public sealed record PasswordResetResult(bool Succeeded, string? ErrorMessage);
