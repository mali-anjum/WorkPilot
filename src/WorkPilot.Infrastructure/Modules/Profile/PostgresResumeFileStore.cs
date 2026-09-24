using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WorkPilot.Application.Modules.Profile.Resumes;
using WorkPilot.Infrastructure.Persistence;

namespace WorkPilot.Infrastructure.Modules.Profile;

/// <summary>One stored resume file (a row in <c>app.resume_files</c>). Written once, never changed or deleted.</summary>
public class StoredResumeFile
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required byte[] Data { get; init; }
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Keeps resume files in Postgres (<c>bytea</c>), because the self hosted Supabase Storage service
/// is not running in this stack yet (spec 0009, Decisions). The row is only added to the
/// DbContext here, so it commits atomically with the version that points at it when the caller saves.
/// Keys look like <c>db:&lt;guid&gt;</c>.
/// </summary>
public sealed class PostgresResumeFileStore(WorkPilotDbContext db) : IResumeFileStore
{
    private const string KeyPrefix = "db:";

    /// <inheritdoc />
    public async Task<string> SaveAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        var file = new StoredResumeFile
        {
            Data = bytes,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = bytes.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
        db.Set<StoredResumeFile>().Add(file);
        return KeyPrefix + file.Id;
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal) || !Guid.TryParse(key[KeyPrefix.Length..], out var id))
        {
            return null;
        }

        return await db.Set<StoredResumeFile>()
            .Where(f => f.Id == id)
            .Select(f => f.Data)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
