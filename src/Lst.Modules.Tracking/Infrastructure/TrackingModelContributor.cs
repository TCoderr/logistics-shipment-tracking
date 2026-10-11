using Lst.Modules.Shipments.Domain;
using Lst.Modules.Tracking.Domain;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Tracking.Infrastructure;

/// <summary>tracking şeması (docs/04 §3.8–3.10, Karar değişikliği Dilim 6: raw_body/raw_sha256, lock_token).</summary>
internal sealed class TrackingModelContributor : IModelContributor
{
    public const string Schema = "tracking";
    public const string ReceiptUniqueIndex = "ux_carrier_event_receipts_carrier_event";
    public static readonly Guid SimexCarrierId = new("0192a6c0-5100-7000-8000-00000051e7e0");

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Carrier>(b =>
        {
            b.ToTable("carriers", Schema, t => t.HasCheckConstraint("ck_carriers_code_format", "code ~ '^[A-Z0-9]{2,16}$'"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(16).IsRequired();
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_carriers_code");
            // Referans veri: tek simüle carrier (SIMEX). Gerçek carrier entegrasyonu yoktur; secret'ı konfigürasyondadır.
            b.HasData(new Carrier(SimexCarrierId, "SIMEX", "SimEx Kargo (simülasyon)", sendsSequence: true));
        });

        modelBuilder.Entity<CarrierEventReceipt>(b =>
        {
            b.ToTable("carrier_event_receipts", Schema, t =>
            {
                t.HasCheckConstraint("ck_receipts_payload_sha256", "octet_length(payload_sha256) = 32");
                t.HasCheckConstraint("ck_receipts_raw_sha256", "octet_length(raw_sha256) = 32");
                t.HasCheckConstraint("ck_receipts_status", "processing_status IN ('Received','Processing','Processed','DeadLettered')");
                t.HasCheckConstraint("ck_receipts_outcome", "outcome IS NULL OR outcome IN ('Applied','Duplicate','Stale','Rejected','PendingReview')");
                t.HasCheckConstraint("ck_receipts_processed_has_outcome", "(processing_status = 'Processed') = (outcome IS NOT NULL)");
                t.HasCheckConstraint("ck_receipts_processing_has_lease", "processing_status <> 'Processing' OR (lock_token IS NOT NULL AND locked_until IS NOT NULL)");
                t.HasCheckConstraint("ck_receipts_attempts", "attempts >= 0");
                t.HasCheckConstraint("ck_receipts_sequence", "carrier_sequence IS NULL OR carrier_sequence >= 0");
                // Ham gövde yalnız saklama süresi sonunda temizlenebilir; temizlenince zaman damgası zorunlu.
                t.HasCheckConstraint("ck_receipts_raw_or_purged", "raw_body IS NOT NULL OR payload_purged_at IS NOT NULL");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ExternalEventId).HasMaxLength(100).IsRequired();
            b.Property(x => x.PayloadSha256).IsRequired();
            b.Property(x => x.RawSha256).IsRequired();
            b.Property(x => x.Payload).HasColumnType("jsonb");
            b.Property(x => x.TrackingNumber).HasMaxLength(12);
            b.Property(x => x.EventType).HasMaxLength(32);
            b.Property(x => x.ProcessingStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(x => x.Outcome).HasMaxLength(16);
            b.Property(x => x.OutcomeReason).HasMaxLength(64);
            b.Property(x => x.LockedBy).HasMaxLength(64);
            b.Property(x => x.LastError).HasMaxLength(2000);
            b.HasOne<Carrier>().WithMany().HasForeignKey(x => x.CarrierId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Shipment>().WithMany().HasForeignKey(x => x.ShipmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => new { x.CarrierId, x.ExternalEventId }).IsUnique().HasDatabaseName(ReceiptUniqueIndex);
            b.HasIndex(x => x.NextAttemptAt).HasFilter("processing_status IN ('Received','Processing')").HasDatabaseName("ix_receipts_pending");
            b.HasIndex(x => new { x.ShipmentId, x.ReceivedAt }).HasDatabaseName("ix_receipts_shipment");
            b.HasIndex(x => new { x.TrackingNumber, x.ReceivedAt }).HasDatabaseName("ix_receipts_tracking_number");
        });

        modelBuilder.Entity<CarrierEventConflict>(b =>
        {
            b.ToTable("carrier_event_conflicts", Schema, t =>
            {
                t.HasCheckConstraint("ck_conflicts_payload_sha256", "octet_length(payload_sha256) = 32");
                t.HasCheckConstraint("ck_conflicts_raw_or_purged", "raw_body IS NOT NULL OR payload_purged_at IS NOT NULL");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PayloadSha256).IsRequired();
            b.Property(x => x.RawSha256).IsRequired();
            b.HasOne<CarrierEventReceipt>().WithMany().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.ReceiptId).HasDatabaseName("ix_carrier_event_conflicts_receipt");
        });

        // Gönderi → carrier FK'si burada tanımlanır (Shipments, Tracking tiplerini bilmez).
        modelBuilder.Entity<Shipment>().HasOne<Carrier>().WithMany().HasForeignKey(s => s.CarrierId).OnDelete(DeleteBehavior.Restrict);

        // tracking_events.carrier_event_receipt_id: Dilim 1'de FK'siz bırakıldı; makbuzlar silinmediği (tombstone, Dilim 8) için FK eklenir.
        modelBuilder.Entity<TrackingEvent>().HasOne<CarrierEventReceipt>().WithMany().HasForeignKey(e => e.CarrierEventReceiptId).OnDelete(DeleteBehavior.Restrict);
    }
}
