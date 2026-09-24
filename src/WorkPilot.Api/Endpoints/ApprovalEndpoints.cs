using WorkPilot.Application.Modules.Approvals;
using WorkPilot.Contracts.Approvals;

namespace WorkPilot.Api.Endpoints;

/// <summary>
/// The approval engine's internal endpoints (spec 0007, building on spec
/// 0005's decide path). Internal only, like every Api route: the Api is never
/// externally exposed (see AppHost.cs), so the Web host, which derives the
/// profile from its session cookie, is the only caller.
/// </summary>
public static class ApprovalEndpoints
{
    /// <summary>Maps <c>GET /internal/approvals</c> and <c>POST /internal/agent/approvals/{id}/decide</c>.</summary>
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        // The Approval center view for one profile: pending approvals with
        // their evidence, plus recent decisions (AC-4).
        app.MapGet("/internal/approvals", async (
            Guid? profileId,
            GetApprovalCenterHandler handler,
            CancellationToken cancellationToken) =>
        {
            if (profileId is not { } id || id == Guid.Empty)
            {
                return Results.BadRequest();
            }

            return Results.Ok(await handler.HandleAsync(id, cancellationToken));
        });

        // Same URL and response shape as spec 0005's decide endpoint, now on
        // the DecideApprovalHandler use case: owner check (403), typed
        // confirmation for the explicit tier (422), and a single guarded
        // transaction (409 on a second or concurrent decision).
        app.MapPost("/internal/agent/approvals/{id:guid}/decide", async (
            Guid id,
            DecideApprovalRequest request,
            DecideApprovalHandler handler,
            CancellationToken cancellationToken) =>
        {
            var result = await handler.HandleAsync(
                new DecideApprovalCommand(id, request.Decision, request.DecidedBy, request.Confirmation),
                cancellationToken);

            return result.Outcome switch
            {
                DecideApprovalOutcome.Decided => Results.Ok(new DecideApprovalResponse(result.ApprovalId, result.Status!.Value.ToString(), result.Resumed)),
                DecideApprovalOutcome.InvalidDecision => Results.BadRequest(),
                DecideApprovalOutcome.NotFound => Results.NotFound(),
                DecideApprovalOutcome.NotOwner => Results.StatusCode(StatusCodes.Status403Forbidden),
                DecideApprovalOutcome.ConfirmationRequired => Results.UnprocessableEntity(new { error = "ConfirmationRequired" }),
                DecideApprovalOutcome.AlreadyDecided => Results.Conflict(new { error = "AlreadyDecided" }),
                DecideApprovalOutcome.ConcurrentChange => Results.Conflict(new { error = "ConcurrentChange" }),
                _ => throw new InvalidOperationException($"Unhandled decide outcome {result.Outcome}."),
            };
        });

        return app;
    }
}
