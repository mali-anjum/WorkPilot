using Microsoft.AspNetCore.Mvc;
using WorkPilot.Application.Modules.Profile.Resumes;
using WorkPilot.Domain.Modules.Profile;

namespace WorkPilot.Api.Endpoints;

/// <summary>
/// Resume management endpoints (spec 0009). Internal only, like every other Api endpoint: the Api is
/// never externally exposed (spec 0004), so the caller (the Web host) passes the signed in
/// <c>profileId</c> and every call is scoped to it (AC-9).
/// </summary>
public static class ResumeEndpoints
{
    /// <summary>
    /// The largest create or revise request accepted: the 5 MB file plus room for the text fields
    /// (100,000 characters, up to 4 bytes each). Enforced before the body is buffered, so an oversized
    /// upload is refused with <c>413</c> instead of being read into memory first (AC-8).
    /// </summary>
    public const long MaxUploadRequestBytes = ResumeRules.FileMaxBytes + (1024 * 1024);

    /// <summary>Maps the <c>/internal/resumes</c> endpoints.</summary>
    public static IEndpointRouteBuilder MapResumeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/resumes");

        // Lists the profile's resumes, most recently updated first (AC-1).
        group.MapGet("/", async (Guid? profileId, IResumeService resumes, CancellationToken ct) =>
            profileId is { } id && id != Guid.Empty
                ? Results.Ok(await resumes.ListAsync(id, ct))
                : Results.ValidationProblem(Error("profileId", "profileId is required.")));

        // One resume with its full version history, newest first (AC-7).
        group.MapGet("/{id:guid}", async (Guid id, Guid profileId, IResumeService resumes, CancellationToken ct) =>
            await resumes.GetAsync(profileId, id, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());

        // Creates a base resume (multipart: profileId, name, content, note?, file?) (AC-1, AC-8).
        group.MapPost("/", async (HttpRequest request, IResumeService resumes, CancellationToken ct) =>
        {
            var (form, tooLarge) = await ReadFormAsync(request, ct);
            if (tooLarge)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            if (form is null || !TryGetGuid(form, "profileId", out var profileId))
            {
                return Results.ValidationProblem(Error("profileId", "A multipart form with profileId is required."));
            }

            var file = form.Files.GetFile("file");
            await using var stream = file?.OpenReadStream();
            var result = await resumes.CreateAsync(
                new CreateResumeCommand(profileId, form["name"], form["content"], form["note"], ToUpload(file, stream)),
                ct);
            return ToHttp(result, detail => Results.Created($"/internal/resumes/{detail.Id}", detail));
        }).WithUploadLimit();

        // Creates a tailored resume copied from one of the profile's versions (AC-6).
        group.MapPost("/tailored", async ([FromBody] TailorResumeRequest body, IResumeService resumes, CancellationToken ct) =>
        {
            var result = await resumes.TailorAsync(
                new TailorResumeCommand(body.ProfileId, body.SourceVersionId, body.Name, body.TargetCompany, body.Note),
                ct);
            return ToHttp(result, detail => Results.Created($"/internal/resumes/{detail.Id}", detail));
        });

        // Edits a resume (multipart: profileId, content, note?, file?, removeFile?): the draft in place,
        // or a new version when the newest is locked (AC-2, AC-4, AC-10). 409 on a lost race (AC-5).
        group.MapPost("/{id:guid}/revisions", async (Guid id, HttpRequest request, IResumeService resumes, CancellationToken ct) =>
        {
            var (form, tooLarge) = await ReadFormAsync(request, ct);
            if (tooLarge)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            if (form is null || !TryGetGuid(form, "profileId", out var profileId))
            {
                return Results.ValidationProblem(Error("profileId", "A multipart form with profileId is required."));
            }

            var file = form.Files.GetFile("file");
            await using var stream = file?.OpenReadStream();
            var removeFile = bool.TryParse(form["removeFile"], out var remove) && remove;
            var result = await resumes.ReviseAsync(
                new ReviseResumeCommand(profileId, id, form["content"], form["note"], ToUpload(file, stream), removeFile),
                ct);
            return ToHttp(result, Results.Ok);
        }).WithUploadLimit();

        // Locks a version because an application used it; idempotent (AC-3). Feature 17's hook.
        group.MapPost("/versions/{versionId:guid}/lock", async (Guid versionId, [FromBody] LockResumeVersionRequest body, IResumeService resumes, CancellationToken ct) =>
            ToHttp(await resumes.LockVersionAsync(body.ProfileId, versionId, body.ApplicationId, ct), Results.Ok));

        // Downloads a version's stored file, always as an attachment (AC-7).
        group.MapGet("/versions/{versionId:guid}/file", async (Guid versionId, Guid profileId, IResumeService resumes, CancellationToken ct) =>
            await resumes.GetFileAsync(profileId, versionId, ct) is { } file
                ? Results.File(file.Content, file.ContentType, file.FileName)
                : Results.NotFound());

        return app;
    }

    private static RouteHandlerBuilder WithUploadLimit(this RouteHandlerBuilder builder) =>
        builder
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadRequestBytes))
            .WithFormOptions(multipartBodyLengthLimit: MaxUploadRequestBytes);

    // Reads the multipart form, reporting an over the limit body instead of throwing: Kestrel signals
    // it with a 413 BadHttpRequestException, the form reader with an InvalidDataException.
    private static async Task<(IFormCollection? Form, bool TooLarge)> ReadFormAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return (null, false);
        }

        try
        {
            return (await request.ReadFormAsync(ct), false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, true);
        }
        catch (InvalidDataException)
        {
            return (null, true);
        }
    }

    private static bool TryGetGuid(IFormCollection form, string key, out Guid value) =>
        Guid.TryParse(form[key], out value) && value != Guid.Empty;

    private static UploadedResumeFile? ToUpload(IFormFile? file, Stream? stream) =>
        file is null || stream is null ? null : new UploadedResumeFile(file.FileName, file.Length, stream);

    private static Dictionary<string, string[]> Error(string field, string message) => new() { [field] = [message] };

    private static IResult ToHttp<T>(ResumeResult<T> result, Func<T, IResult> ok) => result.Status switch
    {
        ResumeResultStatus.Ok => ok(result.Value!),
        ResumeResultStatus.NotFound => Results.NotFound(),
        ResumeResultStatus.Conflict => Results.Conflict(new { errors = result.Errors }),
        _ => Results.ValidationProblem(result.Errors?.ToDictionary() ?? []),
    };
}

/// <summary>Request body for <c>POST /internal/resumes/tailored</c>.</summary>
public sealed record TailorResumeRequest(Guid ProfileId, Guid SourceVersionId, string? Name, string? TargetCompany, string? Note);

/// <summary>Request body for <c>POST /internal/resumes/versions/{versionId}/lock</c>.</summary>
public sealed record LockResumeVersionRequest(Guid ProfileId, Guid ApplicationId);
