using WorkPilot.Application.Modules.Approvals;
using WorkPilot.Contracts.Approvals;

namespace WorkPilot.Api.Endpoints;

/// <summary>
/// The approval engine's internal endpoints (spec 0007, building on spec
/// 0005's decide path). Internal only, like every Api route: the Api is never
/// externally exposed (see AppHost.cs), so the Web host, which derives the
/// profile from its session cookie, is the only caller.
/// </summary>
public static class ApprovalsEndpoints
{
    /// <summary>Maps <c>GET /internal/approvals</c> and <c>POST /internal/agent/approvals/{id}/decide</c>.</summary>
    public static IEndpointRouteBuilder MapApprovalsEndpoints(this IEndpointRouteBuilder app)
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

        // Same URL and status codes as spec 0005's decide endpoint, now on
        // the DecideApprovalHandler use case, every failure as ProblemDetails
        // (spec 0018, section 4): owner check (403), typed
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
                DecideApprovalOutcome.InvalidDecision => Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["decision"] = [$"The decision must be {DecideApprovalHandler.Approve} or {DecideApprovalHandler.Reject}."] }),
                DecideApprovalOutcome.NotFound => Results.Problem(detail: "That approval does not exist.", statusCode: StatusCodes.Status404NotFound),
                DecideApprovalOutcome.NotOwner => Results.Problem(detail: "Only the run's owner can decide this approval.", statusCode: StatusCodes.Status403Forbidden),
                DecideApprovalOutcome.ConfirmationRequired => Results.Problem(detail: "Type the confirmation phrase to approve this action.", statusCode: StatusCodes.Status422UnprocessableEntity),
                DecideApprovalOutcome.AlreadyDecided => Results.Problem(detail: "This approval was already decided.", statusCode: StatusCodes.Status409Conflict),
                DecideApprovalOutcome.ConcurrentChange => Results.Problem(detail: "The run changed while you decided. Try again.", statusCode: StatusCodes.Status409Conflict),
                _ => throw new InvalidOperationException($"Unhandled decide outcome {result.Outcome}."),
            };
        });

        return app;
    }
}
