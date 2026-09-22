using Microsoft.Extensions.Logging;
using Supabase;
using WorkPilot.Application.Modules.Identity;

namespace WorkPilot.Infrastructure.Modules.Identity;

/// <summary>
/// Calls GoTrue (via the official Supabase .NET client) for exactly three
/// things: check a password, send a recovery email, complete a recovery.
/// Never persists a GoTrue access or refresh token; the <see cref="Client"/>
/// instance is request scoped so nothing it holds outlives the call.
/// </summary>
public sealed class SupabaseAuthService(Client client, ILogger<SupabaseAuthService> logger) : IAuthService
{
    public async Task<SignInResult> SignInAsync(string email, string password, CancellationToken cancellationToken)
    {
        try
        {
            var session = await client.Auth.SignInWithPassword(email, password);
            if (session?.User?.Id is null)
            {
                return new SignInResult(false, null, null);
            }

            return new SignInResult(true, Guid.Parse(session.User.Id), session.User.Email);
        }
        catch (Exception ex)
        {
            // GoTrue returns one generic rejection for "unknown email", "wrong
            // password", and "unconfirmed account" alike (spec 0004, AC-2, so
            // the app never distinguishes them either; this log line is for
            // the operator, never for the visitor.
            logger.LogInformation(ex, "GoTrue sign in rejected for {Email}", email);
            return new SignInResult(false, null, null);
        }
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken)
    {
        try
        {
            await client.Auth.ResetPasswordForEmail(email);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose (spec 0004, AC-5): the confirmation message
            // shown to the caller never varies by whether this succeeded, so a
            // rate limited or unknown-email rejection here changes nothing
            // observable; still logged for the operator.
            logger.LogInformation(ex, "GoTrue password reset request failed for {Email}", email);
        }
    }

    public async Task<PasswordResetResult> ResetPasswordAsync(string tokenHash, string newPassword, CancellationToken cancellationToken)
    {
        try
        {
            var session = await client.Auth.VerifyTokenHash(tokenHash, Supabase.Gotrue.Constants.EmailOtpType.Recovery);
            if (session?.User is null)
            {
                return new PasswordResetResult(false, "This reset link is invalid or has expired.");
            }

            await client.Auth.Update(new Supabase.Gotrue.UserAttributes { Password = newPassword });
            return new PasswordResetResult(true, null);
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "GoTrue password reset completion failed");
            return new PasswordResetResult(false, "This reset link is invalid or has expired.");
        }
    }
}
