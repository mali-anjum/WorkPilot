using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WorkPilot.Application.Modules.Profile.Resumes;

namespace WorkPilot.Web.Features.Resumes;

/// <summary>A file picked in the browser, already read into memory, to send with a create or revise.</summary>
public sealed record ResumeUpload(string FileName, byte[] Content);

/// <summary>The outcome of a resume write made through the Api: a value, or a message to show the user.</summary>
public sealed record ResumeApiResult<T>(T? Value, string? Error)
{
    public bool Succeeded => Error is null && Value is not null;
}

/// <summary>
/// The Web host's typed client for the internal resume endpoints (spec 0009). Pages call this rather
/// than the Api directly, since only the Web server can reach the internal Api (spec 0004).
/// </summary>
public interface IResumesApiClient
{
    Task<IReadOnlyList<ResumeSummaryDto>> ListAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task<ResumeDetailDto?> GetAsync(Guid profileId, Guid resumeId, CancellationToken cancellationToken = default);

    Task<ResumeApiResult<ResumeDetailDto>> CreateAsync(Guid profileId, string name, string content, string? note, ResumeUpload? file, CancellationToken cancellationToken = default);

    Task<ResumeApiResult<ReviseResumeResultDto>> ReviseAsync(Guid profileId, Guid resumeId, string content, string? note, ResumeUpload? file, bool removeFile, CancellationToken cancellationToken = default);

    Task<ResumeApiResult<ResumeDetailDto>> TailorAsync(Guid profileId, Guid sourceVersionId, string name, string targetCompany, CancellationToken cancellationToken = default);

    /// <summary>The raw Api response for a version's file (the caller streams and disposes it), or null when not found.</summary>
    Task<HttpResponseMessage?> DownloadAsync(Guid profileId, Guid versionId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IResumesApiClient" />
public sealed class ResumesApiClient(IHttpClientFactory httpClientFactory) : IResumesApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Api => httpClientFactory.CreateClient("api");

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResumeSummaryDto>> ListAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        await Api.GetFromJsonAsync<List<ResumeSummaryDto>>($"/internal/resumes?profileId={profileId}", Json, cancellationToken) ?? [];

    /// <inheritdoc />
    public async Task<ResumeDetailDto?> GetAsync(Guid profileId, Guid resumeId, CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync($"/internal/resumes/{resumeId}?profileId={profileId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ResumeDetailDto>(Json, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ResumeApiResult<ResumeDetailDto>> CreateAsync(Guid profileId, string name, string content, string? note, ResumeUpload? file, CancellationToken cancellationToken = default)
    {
        using var form = Form(profileId, content, note, file);
        form.Add(new StringContent(name), "name");
        using var response = await Api.PostAsync("/internal/resumes", form, cancellationToken);
        return await ReadAsync<ResumeDetailDto>(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ResumeApiResult<ReviseResumeResultDto>> ReviseAsync(Guid profileId, Guid resumeId, string content, string? note, ResumeUpload? file, bool removeFile, CancellationToken cancellationToken = default)
    {
        using var form = Form(profileId, content, note, file);
        form.Add(new StringContent(removeFile ? "true" : "false"), "removeFile");
        using var response = await Api.PostAsync($"/internal/resumes/{resumeId}/revisions", form, cancellationToken);
        return await ReadAsync<ReviseResumeResultDto>(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ResumeApiResult<ResumeDetailDto>> TailorAsync(Guid profileId, Guid sourceVersionId, string name, string targetCompany, CancellationToken cancellationToken = default)
    {
        using var response = await Api.PostAsJsonAsync(
            "/internal/resumes/tailored",
            new { profileId, sourceVersionId, name, targetCompany },
            Json,
            cancellationToken);
        return await ReadAsync<ResumeDetailDto>(response, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<HttpResponseMessage?> DownloadAsync(Guid profileId, Guid versionId, CancellationToken cancellationToken = default)
    {
        var response = await Api.GetAsync(
            $"/internal/resumes/versions/{versionId}/file?profileId={profileId}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        response.Dispose();
        return null;
    }

    private static MultipartFormDataContent Form(Guid profileId, string content, string? note, ResumeUpload? file)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(profileId.ToString()), "profileId" },
            { new StringContent(content), "content" },
        };
        if (!string.IsNullOrWhiteSpace(note))
        {
            form.Add(new StringContent(note), "note");
        }

        if (file is not null)
        {
            var bytes = new ByteArrayContent(file.Content);
            bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(bytes, "file", file.FileName);
        }

        return form;
    }

    private static async Task<ResumeApiResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return new ResumeApiResult<T>(await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken), null);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new ResumeApiResult<T>(default, "That resume no longer exists."),
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict => new ResumeApiResult<T>(default, await ErrorMessageAsync(response, cancellationToken)),
            _ => new ResumeApiResult<T>(default, $"The request failed ({(int)response.StatusCode})."),
        };
    }

    // Both a validation problem and the 409 body carry { errors: { field: [messages] } }.
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject()
                    .SelectMany(p => p.Value.EnumerateArray().Select(m => m.GetString()))
                    .Where(m => !string.IsNullOrWhiteSpace(m));
                return string.Join(" ", messages);
            }
        }
        catch (JsonException)
        {
        }

        return "The request was rejected.";
    }
}
