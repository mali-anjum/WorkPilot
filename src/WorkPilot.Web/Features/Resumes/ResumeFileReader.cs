using Microsoft.AspNetCore.Components.Forms;
using WorkPilot.Domain.Modules.Profile;

namespace WorkPilot.Web.Features.Resumes;

/// <summary>Thrown when a picked file is over the upload limit, before anything is sent to the Api.</summary>
public sealed class ResumeFileTooLargeException(string message) : Exception(message);

/// <summary>Reads a browser picked file into memory for upload (spec 0009, AC-8's 5 MB limit).</summary>
public static class ResumeFileReader
{
    /// <summary>Reads <paramref name="file"/> into a <see cref="ResumeUpload"/>, or returns null when none was picked.</summary>
    public static async Task<ResumeUpload?> ReadAsync(IBrowserFile? file, CancellationToken cancellationToken = default)
    {
        if (file is null)
        {
            return null;
        }

        if (file.Size > ResumeRules.FileMaxBytes)
        {
            throw new ResumeFileTooLargeException($"The file must be at most {ResumeRules.FileMaxBytes / (1024 * 1024)} MB.");
        }

        await using var stream = file.OpenReadStream(ResumeRules.FileMaxBytes, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return new ResumeUpload(file.Name, buffer.ToArray());
    }
}
