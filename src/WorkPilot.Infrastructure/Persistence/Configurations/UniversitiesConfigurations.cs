using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Universities;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

public class UniversityConfiguration : IEntityTypeConfiguration<University>
{
    public void Configure(EntityTypeBuilder<University> builder)
    {
        builder.ToTable("universities");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(300).IsRequired();
        builder.OwnsOne(e => e.Provenance, ProvenanceConfigurations.Configure);

        builder.HasMany(e => e.Programs).WithOne().HasForeignKey(p => p.UniversityId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Professors).WithOne().HasForeignKey(p => p.UniversityId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Scholarships).WithOne().HasForeignKey(s => s.UniversityId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class ProgramConfiguration : IEntityTypeConfiguration<Program>
{
    public void Configure(EntityTypeBuilder<Program> builder)
    {
        builder.ToTable("programs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(300).IsRequired();
        builder.OwnsOne(e => e.Provenance, ProvenanceConfigurations.Configure);
    }
}

public class ProfessorConfiguration : IEntityTypeConfiguration<Professor>
{
    public void Configure(EntityTypeBuilder<Professor> builder)
    {
        builder.ToTable("professors");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
        builder.OwnsOne(e => e.Provenance, ProvenanceConfigurations.Configure);

        builder.HasMany(e => e.ResearchAreas).WithMany().UsingEntity<ProfessorResearchArea>(
            j => j.HasOne<ResearchArea>().WithMany().HasForeignKey(pra => pra.ResearchAreaId),
            j => j.HasOne<Professor>().WithMany().HasForeignKey(pra => pra.ProfessorId),
            j =>
            {
                j.HasKey(pra => new { pra.ProfessorId, pra.ResearchAreaId });
                j.ToTable("professor_research_areas");
            });
    }
}

public class ResearchAreaConfiguration : IEntityTypeConfiguration<ResearchArea>
{
    public void Configure(EntityTypeBuilder<ResearchArea> builder)
    {
        builder.ToTable("research_areas");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Name).IsUnique();
        builder.Property(e => e.Name).HasMaxLength(200).IsRequired();
    }
}

public class ScholarshipConfiguration : IEntityTypeConfiguration<Scholarship>
{
    public void Configure(EntityTypeBuilder<Scholarship> builder)
    {
        builder.ToTable("scholarships");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(300).IsRequired();
        builder.OwnsOne(e => e.Provenance, ProvenanceConfigurations.Configure);
    }
}
