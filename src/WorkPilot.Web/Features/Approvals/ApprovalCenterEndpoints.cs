using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using WorkPilot.Web.Client;

namespace WorkPilot.Web.Features.Approvals;

/// <summary>
/// The Approval center's Approve/Reject form target (spec 0007, AC-6). A plain
/// minimal API endpoint, like the auth endpoints, so a decision is an ordinary
/// antiforgery protected form post that works with or without an interactive
/// circuit. The deciding profile always comes from the signed in session's
/// <c>profile_id</c> claim, never from the form.
/// </summary>
public static class ApprovalCenterEndpoints
{
    /// <summary>Registers what the Approval center needs in the Web host.</summary>
    public static IServiceCollection AddApprovalCenter(this IServiceCollection services) =>
        services.AddScoped<IApprovalCenterClient, ApprovalCenterClient>();

    /// <summary>Maps <c>POST /approvals/{id}/decide</c>; requires a signed in session.</summary>
    public static IEndpointRouteBuilder MapApprovalCenterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/approvals/{id:guid}/decide", async (
            Guid id,
            HttpContext ctx,
            IAntiforgery antiforgery,
            IApprovalCenterClient approvals,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAntiforgeryValidAsync(ctx, antiforgery))
            {
                return Results.BadRequest();
            }

            if (!TryGetProfileId(ctx.User, out var profileId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var form = await ctx.Request.ReadFormAsync(cancellationToken);
            var decision = form["decision"].ToString();
            var confirmation = form["confirmation"].ToString();

            var outcome = decision is "Approve" or "Reject"
                ? await approvals.DecideAsync(id, decision, profileId, confirmation, cancellationToken)
                : ApprovalDecisionOutcome.Invalid;

            // Post, redirect, get: a refresh never re-posts the decision.
            return Results.Redirect($"/approvals?outcome={OutcomeQueryValue(outcome)}");
        })
        .RequireAuthorization();

        return app;
    }

    /// <summary>The <c>outcome</c> query value the Approval center page reads back.</summary>
    public static string OutcomeQueryValue(ApprovalDecisionOutcome outcome) => outcome switch
    {
        ApprovalDecisionOutcome.Approved => "approved",
        ApprovalDecisionOutcome.Rejected => "rejected",
        ApprovalDecisionOutcome.AlreadyDecided => "already-decided",
        ApprovalDecisionOutcome.ConfirmationRequired => "confirmation-required",
        ApprovalDecisionOutcome.NotAllowed => "not-allowed",
        ApprovalDecisionOutcome.NotFound => "not-found",
        _ => "invalid",
    };

    private static bool TryGetProfileId(ClaimsPrincipal user, out Guid profileId)
    {
        profileId = Guid.Empty;
        return user.Identity?.IsAuthenticated == true
            && Guid.TryParse(user.FindFirst(PersistedAuthState.ProfileIdClaimType)?.Value, out profileId)
            && profileId != Guid.Empty;
    }

    private static async Task<bool> IsAntiforgeryValidAsync(HttpContext ctx, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(ctx);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}
