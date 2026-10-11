using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lst.Platform.Auditing;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries", Schemas.Platform, t =>
        {
            t.HasCheckConstraint("ck_audit_entries_actor_type", "actor_type IN ('User','Carrier','System','Anonymous')");
            t.HasCheckConstraint("ck_audit_entries_outcome", "outcome IN ('Succeeded','Rejected')");
        });
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).UseIdentityAlwaysColumn();
        builder.Property(a => a.ActorType).HasMaxLength(16).IsRequired();
        builder.Property(a => a.ActorId).HasMaxLength(100).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(64).IsRequired();
        builder.Property(a => a.EntityType).HasMaxLength(64).IsRequired();
        builder.Property(a => a.EntityId).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
        builder.Property(a => a.ReasonCode).HasMaxLength(64);
        builder.Property(a => a.CorrelationId).HasMaxLength(64);
        builder.Property(a => a.Changes).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(a => new { a.EntityType, a.EntityId, a.OccurredAt }).HasDatabaseName("ix_audit_entries_entity");
        builder.HasIndex(a => new { a.ActorId, a.OccurredAt }).HasDatabaseName("ix_audit_entries_actor");
    }
}
