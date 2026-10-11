using Lst.Modules.Shipments.Domain;
using Lst.Platform.Operations;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lst.Modules.Shipments.Infrastructure;

/// <summary>shipments şeması EF eşlemesi (docs/04 §3.2–3.5, Dilim 1 kolon alt kümesi).</summary>
internal sealed class ShipmentsModelContributor : IModelContributor
{
    public const string TrackingNumberUniqueIndex = "ux_shipments_tracking_number";
    public const string PublicTrackingCodeUniqueIndex = "ux_shipments_public_tracking_code";

    private const string StatusValues = "'Created','AtHub','InTransit','OutForDelivery','DeliveryFailed','Delivered','ReturningToSender','ReturnedToSender','Cancelled'";

    public void Configure(ModelBuilder modelBuilder)
    {
        ConfigureShipment(modelBuilder);
        ConfigureParty(modelBuilder);
        ConfigurePackage(modelBuilder);
        ConfigureTrackingEvent(modelBuilder);
        ConfigureDeliveryAttempt(modelBuilder);

        // Tracking modülünün carrier tablosu: Shipments yalnız kod/ad okur (salt-okunur eşleme; migration üretmez).
        modelBuilder.Entity<Application.CarrierRow>(b =>
        {
            b.HasNoKey();
            b.ToView("carriers", "tracking");
        });

        // Operations modülünün okuma sözleşmesi (view migration'da oluşturulur; EF migration üretmez).
        modelBuilder.Entity<Application.OpenShipmentExceptionRow>(b =>
        {
            b.HasNoKey();
            b.ToView("v_open_shipment_exceptions", Schemas.Operations);
        });
    }

    private static void ConfigureShipment(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Shipment>(b =>
        {
            b.ToTable("shipments", Schemas.Shipments, t =>
            {
                t.HasCheckConstraint("ck_shipments_tracking_number_format", "tracking_number ~ '^LS[0-9]{10}$'");
                t.HasCheckConstraint("ck_shipments_public_tracking_code_format", "public_tracking_code ~ '^[0-9A-HJKMNP-TV-Z]{26}$'");
                t.HasCheckConstraint("ck_shipments_kind", "kind IN ('Forward','Return')");
                t.HasCheckConstraint("ck_shipments_service_level", "service_level IN ('Standard','Express')");
                t.HasCheckConstraint("ck_shipments_status", $"status IN ({StatusValues})");
                t.HasCheckConstraint("ck_shipments_package_count", "package_count BETWEEN 1 AND 20");
                t.HasCheckConstraint("ck_shipments_delivery_attempt_count", "delivery_attempt_count >= 0");
                t.HasCheckConstraint("ck_shipments_declared_value_pair", "(declared_value_amount IS NULL) = (declared_value_currency IS NULL)");
                t.HasCheckConstraint("ck_shipments_declared_value_amount", "declared_value_amount IS NULL OR declared_value_amount >= 0");
                t.HasCheckConstraint("ck_shipments_declared_value_currency", "declared_value_currency IS NULL OR declared_value_currency ~ '^[A-Z]{3}$'");
                t.HasCheckConstraint("ck_shipments_at_hub_requires_hub", "status <> 'AtHub' OR current_hub_id IS NOT NULL");
                t.HasCheckConstraint("ck_shipments_cancelled_requires_reason", "status <> 'Cancelled' OR cancellation_reason IS NOT NULL");
                t.HasCheckConstraint("ck_shipments_extra_attempts", "extra_attempts_granted >= 0");
                t.HasCheckConstraint("ck_shipments_delivered_at", "(status = 'Delivered') = (delivered_at IS NOT NULL)");
            });

            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.TrackingNumber).HasMaxLength(12).IsRequired();
            b.Property(s => s.PublicTrackingCode).HasMaxLength(26).IsRequired();
            b.Property(s => s.Kind).HasConversion<string>().HasMaxLength(16).IsRequired().HasDefaultValue(ShipmentKind.Forward);
            b.Property(s => s.ExternalReference).HasMaxLength(64);
            b.Property(s => s.ServiceLevel).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(s => s.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            // Saklama işi aday taraması: terminal + maskelenmemiş gönderiler.
            b.HasIndex(s => s.StatusChangedAt).HasFilter("pii_redacted_at IS NULL AND status IN ('Delivered','ReturnedToSender','Cancelled')")
                .HasDatabaseName("ix_shipments_retention_candidates");
            b.Property(s => s.DeclaredValueAmount).HasPrecision(12, 2);
            b.Property(s => s.DeclaredValueCurrency).HasColumnType("character(3)");
            b.Property(s => s.DeliveryAttemptCount).HasDefaultValue((short)0);
            b.Property(s => s.ExtraAttemptsGranted).HasDefaultValue((short)0);
            b.Property(s => s.HasDeparted).HasDefaultValue(false);
            b.Property(s => s.CancellationReason).HasMaxLength(500);
            b.HasIndex(s => new { s.CarrierId, s.Status }).HasFilter("carrier_id IS NOT NULL").HasDatabaseName("ix_shipments_carrier_status");
            b.Property(s => s.Version).IsRowVersion();

            b.HasIndex(s => s.TrackingNumber).IsUnique().HasDatabaseName(TrackingNumberUniqueIndex);
            b.HasIndex(s => s.PublicTrackingCode).IsUnique().HasDatabaseName(PublicTrackingCodeUniqueIndex);
            b.HasIndex(s => new { s.CreatedAt, s.Id }).IsDescending(true, true).HasDatabaseName("ix_shipments_created_at_id");
            b.HasIndex(s => new { s.Status, s.PromisedDeliveryDate }).HasDatabaseName("ix_shipments_status_promised");
            // Dilim 3 keyset sıralamaları: (sıralama alanı, id).
            b.HasIndex(s => new { s.PromisedDeliveryDate, s.Id }).HasDatabaseName("ix_shipments_promised_id");
            b.HasIndex(s => new { s.StatusChangedAt, s.Id }).IsDescending(true, true).HasDatabaseName("ix_shipments_status_changed_id");
            b.HasIndex(s => new { s.CurrentHubId, s.Status }).HasDatabaseName("ix_shipments_current_hub_status");
            b.HasIndex(s => new { s.DestinationHubId, s.Status }).HasDatabaseName("ix_shipments_destination_hub_status");
            b.HasIndex(s => new { s.OriginHubId, s.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_shipments_origin_hub_created_at");
            b.HasIndex(s => s.ExternalReference).HasFilter("external_reference IS NOT NULL").HasDatabaseName("ix_shipments_external_reference");

            b.HasOne<Hub>().WithMany().HasForeignKey(s => s.OriginHubId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Hub>().WithMany().HasForeignKey(s => s.DestinationHubId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Hub>().WithMany().HasForeignKey(s => s.CurrentHubId).OnDelete(DeleteBehavior.Restrict);

            b.HasMany(s => s.Parties).WithOne().HasForeignKey(p => p.ShipmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(s => s.Packages).WithOne().HasForeignKey(p => p.ShipmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(s => s.TrackingEvents).WithOne().HasForeignKey(e => e.ShipmentId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(s => s.DeliveryAttempts).WithOne().HasForeignKey(a => a.ShipmentId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(s => s.DeliveryAttempts).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasIndex(s => s.DeliveredAt).HasFilter("delivered_at IS NOT NULL").HasDatabaseName("ix_shipments_delivered_at");
            b.Navigation(s => s.Parties).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(s => s.Packages).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(s => s.TrackingEvents).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Ignore(s => s.Sender);
            b.Ignore(s => s.Recipient);
        });

    private static void ConfigureParty(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<ShipmentParty>(b =>
        {
            b.ToTable("shipment_parties", Schemas.Shipments, t =>
            {
                t.HasCheckConstraint("ck_shipment_parties_role", "role IN ('Sender','Recipient')");
                t.HasCheckConstraint("ck_shipment_parties_phone_format", "phone_e164 IS NULL OR phone_e164 ~ '^\\+[1-9][0-9]{7,14}$'");
                t.HasCheckConstraint("ck_shipment_parties_country_code", "country_code ~ '^[A-Z]{2}$'");
                // Telefon/adres yalnız PII maskelemesiyle NULL olur; o zaman son 4 hane de NULL'dır.
                t.HasCheckConstraint("ck_shipment_parties_phone_last4", "(phone_e164 IS NULL) = (phone_last4 IS NULL)");
            });
            b.HasKey(p => new { p.ShipmentId, p.Role });
            b.Property(p => p.Role).HasConversion<string>().HasMaxLength(16);
            b.Property(p => p.FullName).HasMaxLength(150).IsRequired();
            b.Property(p => p.CompanyName).HasMaxLength(150);
            b.Property(p => p.PhoneE164).HasMaxLength(16);
            b.Property(p => p.PhoneLast4).HasColumnType("character(4)");
            b.Property(p => p.Email).HasMaxLength(254);
            b.Property(p => p.AddressLine1).HasMaxLength(200);
            b.Property(p => p.AddressLine2).HasMaxLength(200);
            b.Property(p => p.District).HasMaxLength(100);
            b.Property(p => p.City).HasMaxLength(100).IsRequired();
            b.Property(p => p.PostalCode).HasMaxLength(10);
            b.Property(p => p.CountryCode).HasColumnType("character(2)").IsRequired();
            b.HasIndex(p => new { p.Role, p.PhoneLast4 }).HasDatabaseName("ix_shipment_parties_role_phone_last4");
        });

    private static void ConfigurePackage(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Package>(b =>
        {
            b.ToTable("packages", Schemas.Shipments, t =>
            {
                t.HasCheckConstraint("ck_packages_sequence", "sequence BETWEEN 1 AND 20");
                t.HasCheckConstraint("ck_packages_weight_grams", "weight_grams BETWEEN 1 AND 70000");
                t.HasCheckConstraint("ck_packages_dimensions", "length_mm BETWEEN 1 AND 3000 AND width_mm BETWEEN 1 AND 3000 AND height_mm BETWEEN 1 AND 3000");
                t.HasCheckConstraint("ck_packages_last_scan_type", "last_scan_type IS NULL OR last_scan_type IN ('Arrival','Departure','OutForDelivery')");
                t.HasCheckConstraint("ck_packages_scan_pair", "(last_scan_type IS NULL) = (last_scanned_at IS NULL)");
            });
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.Property(p => p.LabelCode).HasMaxLength(15).IsRequired();
            b.Property(p => p.ContentDescription).HasMaxLength(200);
            b.Property(p => p.LastScanType).HasConversion<string>().HasMaxLength(32);
            b.HasOne<Hub>().WithMany().HasForeignKey(p => p.CurrentHubId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(p => p.LabelCode).IsUnique().HasDatabaseName("ux_packages_label_code");
            b.HasIndex(p => new { p.ShipmentId, p.Sequence }).IsUnique().HasDatabaseName("ux_packages_shipment_sequence");
        });

    private static void ConfigureTrackingEvent(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<TrackingEvent>(b =>
        {
            b.ToTable("tracking_events", Schemas.Shipments, t =>
            {
                t.HasCheckConstraint("ck_tracking_events_event_type",
                    "event_type IN ('Created','ArrivedAtHub','DepartedHub','HandedToCarrier','CarrierInTransit','OutForDelivery','DeliveryFailed','Delivered','ReturnInitiated','ReturnedToSender','Cancelled')");
                t.HasCheckConstraint("ck_tracking_events_source", "source IN ('HubScan','Operator','Carrier','System')");
                t.HasCheckConstraint("ck_tracking_events_outcome", "outcome IN ('Applied','Stale','PendingReview')");
                t.HasCheckConstraint("ck_tracking_events_status_before", $"status_before IS NULL OR status_before IN ({StatusValues})");
                t.HasCheckConstraint("ck_tracking_events_status_after", $"status_after IS NULL OR status_after IN ({StatusValues})");
                t.HasCheckConstraint("ck_tracking_events_created_has_no_previous", "(event_type = 'Created') = (status_before IS NULL)");
            });
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).UseIdentityAlwaysColumn();
            b.Property(e => e.EventType).HasConversion<string>().HasMaxLength(32).IsRequired();
            b.Property(e => e.Source).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(e => e.StatusBefore).HasConversion<string>().HasMaxLength(32);
            b.Property(e => e.StatusAfter).HasConversion<string>().HasMaxLength(32);
            b.Property(e => e.LocationCode).HasMaxLength(20);
            b.Property(e => e.LocationCity).HasMaxLength(100);
            b.Property(e => e.ReasonCode).HasMaxLength(64);
            b.Property(e => e.ClientScanId).HasMaxLength(64);
            b.Property(e => e.BusinessKey).HasMaxLength(160).IsRequired();
            b.Property(e => e.InternalNote).HasMaxLength(500);

            b.HasOne<Hub>().WithMany().HasForeignKey(e => e.HubId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Package>().WithMany().HasForeignKey(e => e.PackageId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(e => new { e.ShipmentId, e.OccurredAt, e.Id }).HasDatabaseName("ix_tracking_events_timeline");
            b.HasIndex(e => new { e.ShipmentId, e.BusinessKey, e.OccurredAt }).HasDatabaseName("ix_tracking_events_business_key");
            b.HasIndex(e => new { e.HubId, e.ClientScanId }).IsUnique()
                .HasFilter("client_scan_id IS NOT NULL").HasDatabaseName("ux_tracking_events_hub_client_scan");
            b.HasIndex(e => e.CarrierEventReceiptId).IsUnique()
                .HasFilter("carrier_event_receipt_id IS NOT NULL").HasDatabaseName("ux_tracking_events_carrier_receipt");
        });

    private static void ConfigureDeliveryAttempt(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<DeliveryAttempt>(b =>
        {
            b.ToTable("delivery_attempts", Schemas.Shipments, t =>
            {
                t.HasCheckConstraint("ck_delivery_attempts_number", "attempt_number >= 1");
                t.HasCheckConstraint("ck_delivery_attempts_outcome", "outcome IN ('Delivered','Failed')");
                t.HasCheckConstraint("ck_delivery_attempts_reason",
                    "failure_reason IS NULL OR failure_reason IN ('RecipientAbsent','AddressNotFound','RecipientRefused','AccessRestricted','PackageMissing','PackageDamaged','Other')");
                t.HasCheckConstraint("ck_delivery_attempts_failed_has_reason", "(outcome = 'Failed') = (failure_reason IS NOT NULL)");
                t.HasCheckConstraint("ck_delivery_attempts_other_has_note", "failure_reason IS DISTINCT FROM 'Other' OR note IS NOT NULL");
            });
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).ValueGeneratedNever();
            b.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(a => a.FailureReason).HasConversion<string>().HasMaxLength(32);
            b.Property(a => a.Source).HasConversion<string>().HasMaxLength(16).IsRequired();
            b.Property(a => a.Note).HasMaxLength(500);
            b.HasOne(a => a.TrackingEvent).WithOne().HasForeignKey<DeliveryAttempt>(a => a.TrackingEventId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => a.TrackingEventId).IsUnique().HasDatabaseName("ux_delivery_attempts_tracking_event");
            b.HasIndex(a => new { a.ShipmentId, a.AttemptNumber }).IsUnique().HasDatabaseName("ux_delivery_attempts_shipment_number");
        });
}