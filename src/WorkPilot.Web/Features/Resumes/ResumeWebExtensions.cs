using System.Security.Claims;
using WorkPilot.Web.Client;

namespace WorkPilot.Web.Features.Resumes;

/// <summary>Web host wiring for resume management (spec 0009).</summary>
public static class ResumeWebExtensions
{
    /// <summary>Registers the typed resume Api client the pages use.</summary>
    public static IServiceCollection AddResumeManagementWeb(this IServiceCollection services) =>
        services.AddScoped<IResumesApiClient, ResumesApiClient>();

    /// <summary>
    /// Maps <c>GET /resumes/versions/{versionId}/file</c>: streams a version's stored file from the
    /// internal Api to the signed in founder, always as a download (spec 0009, AC-7).
    /// </summary>
    public static IEndpointRouteBuilder MapResumeFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/resumes/versions/{versionId:guid}/file", async (Guid versionId, HttpContext ctx, IResumesApiClient resumes) =>
        {
            if (ProfileId(ctx.User) is not { } profileId)
            {
                return Results.Forbid();
            }

            var response = await resumes.DownloadAsync(profileId, versionId, ctx.RequestAborted);
            if (response is null)
            {
                return Results.NotFound();
            }

            ctx.Response.RegisterForDispose(response);
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? "resume";
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return Results.Stream(await response.Content.ReadAsStreamAsync(ctx.RequestAborted), contentType, fileName);
        }).RequireAuthorization();

        return app;
    }

    /// <summary>The signed in founder's profile id, from the session cookie's claim (spec 0004).</summary>
    public static Guid? ProfileId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(PersistedAuthState.ProfileIdClaimType)?.Value, out var id) && id != Guid.Empty ? id : null;
}
