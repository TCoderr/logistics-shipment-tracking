using System.Text.Json;
using Lst.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lst.Platform.Outbox;

/// <summary>
/// Outbox payload sözleşmesi. Payload'lar yalnız kimlik, kod, durum ve zaman taşır; kişisel veri (ad, telefon, adres, e-posta, not) asla yazılmaz.
/// Mimari test, uygulayan tüm tiplerin özellik tiplerini ve adlarını denetler.
/// </summary>
public interface IOutboxPayload
{
    /// <summary>Sürümlü mesaj tipi, ör. <c>shipment.status-changed.v1</c>. Payload'a serileştirilmez.</summary>
    static abstract string MessageType { get; }
}

/// <summary>platform.outbox_messages: iş değişikliğiyle aynı transaction'da yazılır, commit sonrası dağıtılır (ADR-017).</summary>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
    }

    public long Id { get; private set; }

    public Guid MessageId { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public string Payload { get; private set; } = "{}";

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset AvailableAt { get; private set; }

    public short Attempts { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public Guid? LockToken { get; private set; }

    public string? LockedBy { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public DateTimeOffset? DeadLetteredAt { get; private set; }

    public string? LastError { get; private set; }

    public short ReplayCount { get; private set; }

    public static OutboxMessage Create<T>(T payload, DateTimeOffset now)
        where T : IOutboxPayload => new()
    {
        MessageId = Guid.CreateVersion7(now),
        Type = T.MessageType,
        Payload = JsonSerializer.Serialize(payload, OutboxJson.Options),
        OccurredAt = now,
        AvailableAt = now,
    };
}

public static class OutboxJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", Schemas.Platform, t =>
        {
            t.HasCheckConstraint("ck_outbox_messages_type", "type ~ '^[a-z][a-z0-9.-]{2,99}$'");
            t.HasCheckConstraint("ck_outbox_messages_attempts", "attempts >= 0");
            t.HasCheckConstraint("ck_outbox_messages_payload_size", "octet_length(payload::text) <= 8192");
            t.HasCheckConstraint("ck_outbox_messages_terminal", "processed_at IS NULL OR dead_lettered_at IS NULL");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityAlwaysColumn();
        builder.Property(x => x.Type).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.LockedBy).HasMaxLength(64);
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.HasIndex(x => x.MessageId).IsUnique().HasDatabaseName("ux_outbox_messages_message_id");
        builder.HasIndex(x => new { x.AvailableAt, x.Id })
            .HasFilter("processed_at IS NULL AND dead_lettered_at IS NULL")
            .HasDatabaseName("ix_outbox_messages_pending");
        builder.HasIndex(x => x.DeadLetteredAt).HasFilter("dead_lettered_at IS NOT NULL").HasDatabaseName("ix_outbox_messages_dead_lettered");
        builder.HasIndex(x => x.ProcessedAt).HasFilter("processed_at IS NOT NULL").HasDatabaseName("ix_outbox_messages_processed");
    }
}

/// <summary>Simüle bildirim kanalının teslim kaydı (tüketici tarafı tekrar ayıklama: message_id tekil). PII içermez.</summary>
public sealed class NotificationDelivery
{
    private NotificationDelivery()
    {
    }

    public NotificationDelivery(Guid messageId, Guid shipmentId, string template, DateTimeOffset recordedAt)
    {
        MessageId = messageId;
        ShipmentId = shipmentId;
        Template = template;
        RecordedAt = recordedAt;
    }

    public Guid MessageId { get; private set; }

    public Guid ShipmentId { get; private set; }

    public string Template { get; private set; } = string.Empty;

    public DateTimeOffset RecordedAt { get; private set; }
}

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries", Schemas.Platform);
        builder.HasKey(x => x.MessageId);
        builder.Property(x => x.Template).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.ShipmentId, x.RecordedAt }).HasDatabaseName("ix_notification_deliveries_shipment");
    }
}
