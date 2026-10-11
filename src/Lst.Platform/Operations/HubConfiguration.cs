using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lst.Platform.Operations;

internal sealed class HubConfiguration : IEntityTypeConfiguration<Hub>
{
    public void Configure(EntityTypeBuilder<Hub> builder)
    {
        builder.ToTable("hubs", Schemas.Operations, t =>
            t.HasCheckConstraint("ck_hubs_code_format", "code ~ '^[A-Z]{3}-[0-9]{2}$'"));
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.Property(h => h.Code).HasMaxLength(10).IsRequired();
        builder.Property(h => h.Name).HasMaxLength(100).IsRequired();
        builder.Property(h => h.City).HasMaxLength(100).IsRequired();
        builder.Property(h => h.District).HasMaxLength(100);
        builder.HasIndex(h => h.Code).IsUnique().HasDatabaseName("ux_hubs_code");
    }
}
