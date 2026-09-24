using System.Net;
using System.Net.Http.Json;
using WorkPilot.Contracts.Approvals;

namespace WorkPilot.Web.Features.Approvals;

/// <summary>What happened to a decision, as the Approval center reports it back to the founder (spec 0007, AC-6).</summary>
public enum ApprovalDecisionOutcome
{
    Approved,
    Rejected,
    AlreadyDecided,
    ConfirmationRequired,
    NotAllowed,
    NotFound,
    Invalid,
}

/// <summary>
/// The Web host's view of the Api's approval endpoints (spec 0007). The Api
/// owns the database; the Web host only ever reaches approvals through here.
/// </summary>
public interface IApprovalCenterClient
{
    /// <summary>The Approval center view for <paramref name="profileId"/>.</summary>
    Task<ApprovalCenterDto> GetAsync(Guid profileId, CancellationToken cancellationToken);

    /// <summary>Decides one approval as <paramref name="decidedBy"/>, who must come from the signed in session.</summary>
    Task<ApprovalDecisionOutcome> DecideAsync(Guid approvalId, string decision, Guid decidedBy, string? confirmation, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IApprovalCenterClient" />
public sealed class ApprovalCenterClient(IHttpClientFactory httpClientFactory) : IApprovalCenterClient
{
    public async Task<ApprovalCenterDto> GetAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var api = httpClientFactory.CreateClient("api");
        return await api.GetFromJsonAsync<ApprovalCenterDto>($"/internal/approvals?profileId={profileId}", cancellationToken)
            ?? throw new InvalidOperationException("The internal approvals endpoint returned no body.");
    }

    public async Task<ApprovalDecisionOutcome> DecideAsync(Guid approvalId, string decision, Guid decidedBy, string? confirmation, CancellationToken cancellationToken)
    {
        var api = httpClientFactory.CreateClient("api");
        var response = await api.PostAsJsonAsync(
            $"/internal/agent/approvals/{approvalId}/decide",
            new DecideApprovalRequest(decision, decidedBy, string.IsNullOrWhiteSpace(confirmation) ? null : confirmation),
            cancellationToken);

        return response.StatusCode switch
        {
            HttpStatusCode.OK => decision == "Approve" ? ApprovalDecisionOutcome.Approved : ApprovalDecisionOutcome.Rejected,
            HttpStatusCode.Conflict => ApprovalDecisionOutcome.AlreadyDecided,
            HttpStatusCode.UnprocessableEntity => ApprovalDecisionOutcome.ConfirmationRequired,
            HttpStatusCode.Forbidden => ApprovalDecisionOutcome.NotAllowed,
            HttpStatusCode.NotFound => ApprovalDecisionOutcome.NotFound,
            HttpStatusCode.BadRequest => ApprovalDecisionOutcome.Invalid,
            _ => throw new HttpRequestException($"Deciding approval {approvalId} failed with {(int)response.StatusCode}.", null, response.StatusCode),
        };
    }
}
