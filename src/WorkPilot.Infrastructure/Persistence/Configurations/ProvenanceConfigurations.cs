using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Common;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

/// <summary>
/// Shared owned type mapping for <see cref="Provenance"/>, applied to every
/// externally sourced entity so the required columns (spec 0002, AC-2) are
/// configured identically everywhere it's used.
/// </summary>
public static class ProvenanceConfigurations
{
    public static void Configure<TOwner>(OwnedNavigationBuilder<TOwner, Provenance> builder)
        where TOwner : class
    {
        builder.Property(p => p.SourceUrl).IsRequired();
        builder.Property(p => p.RetrievedAt).IsRequired();
        builder.Property(p => p.VerifiedAt);
        builder.Property(p => p.Confidence).HasColumnType("numeric(5,4)");
    }
}
