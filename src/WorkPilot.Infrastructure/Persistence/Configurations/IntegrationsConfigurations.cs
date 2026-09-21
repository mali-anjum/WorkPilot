using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkPilot.Domain.Modules.Integrations;

namespace WorkPilot.Infrastructure.Persistence.Configurations;

public class IntegrationConfiguration : IEntityTypeConfiguration<Integration>
{
    public void Configure(EntityTypeBuilder<Integration> builder)
    {
        builder.ToTable("integrations");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ProviderName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(30).IsRequired();
    }
}

// OAuthConnection's own configuration (including the encrypted token
// converter, which needs an IDataProtector instance) is applied directly in
// WorkPilotDbContext.OnModelCreating, not here: IEntityTypeConfiguration
// implementations are constructed parameterless by ApplyConfigurationsFromAssembly,
// and the converter needs a runtime dependency the DbContext already holds.
