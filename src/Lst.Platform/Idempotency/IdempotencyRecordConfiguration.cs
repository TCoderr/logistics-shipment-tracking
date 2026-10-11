using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lst.Platform.Idempotency;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable(IdempotencyStore.TableName, Schemas.Platform, t =>
        {
            t.HasCheckConstraint("ck_idempotency_records_key_format", "idempotency_key ~ '^[A-Za-z0-9_-]{16,128}$'");
            t.HasCheckConstraint("ck_idempotency_records_hash_length", "octet_length(request_hash) = 32");
            t.HasCheckConstraint("ck_idempotency_records_hash_version", "request_hash_version >= 1");
            t.HasCheckConstraint("ck_idempotency_records_expiry", "expires_at > created_at");
            t.HasCheckConstraint(
                "ck_idempotency_records_completion",
                "(completed_at IS NULL AND response_status_code IS NULL AND response_body IS NULL AND response_content_type IS NULL)"
                + " OR (completed_at IS NOT NULL AND response_status_code BETWEEN 200 AND 299 AND response_body IS NOT NULL AND response_content_type IS NOT NULL)");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.ActorScope).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Endpoint).HasMaxLength(100).IsRequired();
        builder.Property(r => r.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(r => r.RequestHash).IsRequired();
        builder.Property(r => r.ResponseContentType).HasMaxLength(100);
        builder.Property(r => r.ResponseLocation).HasMaxLength(200);
        builder.Property(r => r.ResponseEtag).HasMaxLength(64);
        builder.HasIndex(r => new { r.ActorScope, r.Endpoint, r.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName(IdempotencyStore.UniqueIndexName);
        builder.HasIndex(r => r.ExpiresAt).HasDatabaseName("ix_idempotency_records_expires_at");
    }
}
