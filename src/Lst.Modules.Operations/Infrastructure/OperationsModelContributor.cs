using Lst.Modules.Operations.Domain;
using Lst.Modules.Shipments.Domain;
using Lst.Platform.Identity;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Operations.Infrastructure;

/// <summary>operations.shipment_exceptions eşlemesi (docs/04 §3.7). Okuma sözleşmesi view'ı migration'da oluşturulur.</summary>
internal sealed class OperationsModelContributor : IModelContributor
{
    public const string OpenUniqueIndex = "ux_shipment_exceptions_open_per_type";

    public void Configure(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ShipmentException>(b =>
        {
            b.ToTable("shipment_exceptions", Schemas.Operations, t =>
            {
                t.HasCheckConstraint("ck_shipment_exceptions_type",
                    "type IN ('DeliveryFailed','MaxAttemptsReached','DeliveryOverdue','NoMovement','PackageSplit','CarrierEventPendingReview','CarrierEventConflict')");
                t.HasCheckConstraint("ck_shipment_exceptions_severity", "severity IN ('Low','Medium','High')");
                t.HasCheckConstraint("ck_shipment_exceptions_status", "status IN ('Open','Assigned','Resolved')");
                t.HasCheckConstraint("ck_shipment_exceptions_resolution_code",
                    "resolution_code IS NULL OR resolution_code IN ('ReattemptApproved','ReturnToSenderInitiated','CarrierConfirmed','EventAppliedManually','EventDiscarded','PackagesConsolidated','NoActionNeeded','AutoResolved')");
                t.HasCheckConstraint("ck_shipment_exceptions_assigned_has_user", "status <> 'Assigned' OR assigned_to_user_id IS NOT NULL");
                t.HasCheckConstraint("ck_shipment_exceptions_resolved_fields", "(status = 'Resolved') = (resolved_at IS NOT NULL AND resolution_code IS NOT NULL)");
                t.HasCheckConstraint("ck_shipment_exceptions_occurrence", "occurrence_count >= 1");
            });
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(x => x.Severity).HasConversion<string>().HasMaxLength(8).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(x => x.OpenedBySource).HasMaxLength(16).IsRequired();
            b.Property(x => x.ResolutionCode).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.ResolutionNote).HasMaxLength(1000);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.IsOpen);

            b.HasOne<Shipment>().WithMany().HasForeignKey(x => x.ShipmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Hub>().WithMany().HasForeignKey(x => x.HubId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LstUser>().WithMany().HasForeignKey(x => x.AssignedToUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<LstUser>().WithMany().HasForeignKey(x => x.ResolvedByUserId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(x => new { x.ShipmentId, x.Type }).IsUnique()
                .HasFilter("status IN ('Open','Assigned')").HasDatabaseName(OpenUniqueIndex);
            b.HasIndex(x => new { x.DueAt, x.Id }).HasFilter("status <> 'Resolved'").HasDatabaseName("ix_shipment_exceptions_queue_due");
            b.HasIndex(x => new { x.OpenedAt, x.Id }).IsDescending(true, true).HasDatabaseName("ix_shipment_exceptions_opened");
            b.HasIndex(x => new { x.AssignedToUserId, x.Status }).HasFilter("status = 'Assigned'").HasDatabaseName("ix_shipment_exceptions_assignee");
            b.HasIndex(x => new { x.HubId, x.Status }).HasDatabaseName("ix_shipment_exceptions_hub_status");
        });
}
