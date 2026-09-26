using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.NotificationService.Domain.Entities;

namespace Sellora.NotificationService.Infrastructure.Persistence.Configurations;

public sealed class NotificationRequestConfiguration : IEntityTypeConfiguration<NotificationRequest>
{
    /// <summary>US-E5-1-T3: one request per event, whatever Kafka redelivers.</summary>
    public const string SourceEventUniqueIndex = "uq_notification_request_source_event";

    public void Configure(EntityTypeBuilder<NotificationRequest> builder)
    {
        builder.ToTable("notification_request", table =>
            table.HasCheckConstraint("ck_notification_request_status", "status IN ('Pending')"));

        builder.HasKey(request => request.NotificationRequestId).HasName("pk_notification_request");

        builder.Property(request => request.NotificationRequestId).HasColumnName("notification_request_id").ValueGeneratedNever();
        builder.Property(request => request.CompanyId).HasColumnName("company_id").IsRequired();
        builder.Property(request => request.SourceEventId).HasColumnName("source_event_id").IsRequired();
        builder.Property(request => request.EventType).HasColumnName("event_type").HasMaxLength(50).IsRequired();
        builder.Property(request => request.TemplateKey).HasColumnName("template_key").HasMaxLength(50).IsRequired();
        builder.Property(request => request.OrderId).HasColumnName("order_id").IsRequired();
        builder.Property(request => request.OrderReference).HasColumnName("order_reference")
            .HasMaxLength(NotificationRequest.MaxReferenceLength).IsRequired();
        builder.Property(request => request.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(request => request.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        builder.Property(request => request.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
        builder.Property(request => request.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(request => request.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(request => request.SourceTopic).HasColumnName("source_topic").HasMaxLength(249).IsRequired();
        builder.Property(request => request.SourcePartition).HasColumnName("source_partition").IsRequired();
        builder.Property(request => request.SourceOffset).HasColumnName("source_offset").IsRequired();

        // Event IDs are GUIDs minted by the producer's outbox: unique across
        // companies, so the index is too — no tenant column needed in it.
        builder.HasIndex(request => request.SourceEventId).IsUnique().HasDatabaseName(SourceEventUniqueIndex);

        builder.HasIndex(request => new { request.CompanyId, request.OrderId })
            .HasDatabaseName("ix_notification_request_company_order");

        // US-E5-2's dispatcher will poll for Pending work.
        builder.HasIndex(request => new { request.Status, request.ReceivedAt })
            .HasDatabaseName("ix_notification_request_status_received");

        builder.HasMany(request => request.Recipients)
            .WithOne()
            .HasForeignKey(recipient => recipient.NotificationRequestId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_notification_recipient_request");

        builder.Navigation(request => request.Recipients)
            .HasField("_recipients")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class NotificationRecipientConfiguration : IEntityTypeConfiguration<NotificationRecipient>
{
    public void Configure(EntityTypeBuilder<NotificationRecipient> builder)
    {
        builder.ToTable("notification_recipient", table =>
            table.HasCheckConstraint("ck_notification_recipient_kind", "kind IN ('Shop', 'Agency')"));

        builder.HasKey(recipient => recipient.NotificationRecipientId).HasName("pk_notification_recipient");

        builder.Property(recipient => recipient.NotificationRecipientId).HasColumnName("notification_recipient_id").ValueGeneratedNever();
        builder.Property(recipient => recipient.NotificationRequestId).HasColumnName("notification_request_id").IsRequired();
        builder.Property(recipient => recipient.CompanyId).HasColumnName("company_id").IsRequired();
        builder.Property(recipient => recipient.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(recipient => recipient.RecipientId).HasColumnName("recipient_id").IsRequired();
        builder.Property(recipient => recipient.Name).HasColumnName("name").HasMaxLength(NotificationRecipient.MaxNameLength);
        builder.Property(recipient => recipient.Email).HasColumnName("email").HasMaxLength(NotificationRecipient.MaxEmailLength);

        builder.HasIndex(recipient => new { recipient.NotificationRequestId, recipient.Kind })
            .IsUnique()
            .HasDatabaseName("uq_notification_recipient_request_kind");
    }
}
