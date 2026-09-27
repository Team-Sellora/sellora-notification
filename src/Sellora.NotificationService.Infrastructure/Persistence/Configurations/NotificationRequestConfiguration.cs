using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.NotificationService.Domain.Entities;
using Sellora.NotificationService.Domain.Notifications;

namespace Sellora.NotificationService.Infrastructure.Persistence.Configurations;

public sealed class NotificationRequestConfiguration : IEntityTypeConfiguration<NotificationRequest>
{
    /// <summary>US-E5-1-T3: one request per event, whatever Kafka redelivers.</summary>
    public const string SourceEventUniqueIndex = "uq_notification_request_source_event";

    public void Configure(EntityTypeBuilder<NotificationRequest> builder)
    {
        builder.ToTable("notification_request", table =>
            table.HasCheckConstraint("ck_notification_request_status", "status IN ('Pending', 'Sent', 'PartiallySent', 'Failed', 'PermanentlyFailed')"));

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

        // US-E5-2: the message exactly as sent, and how the dispatch went.
        builder.Property(request => request.RenderedSubject).HasColumnName("rendered_subject").HasMaxLength(500);
        builder.Property(request => request.RenderedHtml).HasColumnName("rendered_html").HasColumnType("text");
        builder.Property(request => request.RenderedText).HasColumnName("rendered_text").HasColumnType("text");
        builder.Property(request => request.RenderedBodySha256).HasColumnName("rendered_body_sha256").HasMaxLength(64);
        builder.Property(request => request.RenderedAt).HasColumnName("rendered_at").HasColumnType("timestamp with time zone");
        builder.Property(request => request.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(request => request.LastAttemptAt).HasColumnName("last_attempt_at").HasColumnType("timestamp with time zone");
        builder.Property(request => request.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamp with time zone");
        builder.Property(request => request.CompletedAt).HasColumnName("completed_at").HasColumnType("timestamp with time zone");
        builder.Property(request => request.SendGapMilliseconds).HasColumnName("send_gap_ms");
        builder.Property(request => request.ClaimedUntil).HasColumnName("claimed_until").HasColumnType("timestamp with time zone");
        builder.Ignore(request => request.IsRendered);
        builder.Ignore(request => request.Rendered);
        builder.Ignore(request => request.FailureReason);

        // US-E5-3: the last manual resend.
        builder.Property(request => request.LastResendAt).HasColumnName("last_resend_at").HasColumnType("timestamp with time zone");
        builder.Property(request => request.LastResendBy).HasColumnName("last_resend_by").HasMaxLength(200);

        builder.HasMany(request => request.AttemptHistory)
            .WithOne()
            .HasForeignKey(attempt => attempt.NotificationRequestId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_notification_attempt_request");

        builder.Navigation(request => request.AttemptHistory)
            .HasField("_attempts")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // The dispatcher's work queue: due requests in arrival order.
        builder.HasIndex(request => new { request.Status, request.NextAttemptAt })
            .HasDatabaseName("ix_notification_request_dispatch_due");

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
        {
            table.HasCheckConstraint("ck_notification_recipient_kind", "kind IN ('Shop', 'Agency')");
            table.HasCheckConstraint(
                "ck_notification_recipient_delivery_status",
                "delivery_status IN ('Pending', 'Sent', 'Failed', 'Unaddressed', 'PermanentlyFailed')");
        });

        builder.HasKey(recipient => recipient.NotificationRecipientId).HasName("pk_notification_recipient");

        builder.Property(recipient => recipient.NotificationRecipientId).HasColumnName("notification_recipient_id").ValueGeneratedNever();
        builder.Property(recipient => recipient.NotificationRequestId).HasColumnName("notification_request_id").IsRequired();
        builder.Property(recipient => recipient.CompanyId).HasColumnName("company_id").IsRequired();
        builder.Property(recipient => recipient.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(recipient => recipient.RecipientId).HasColumnName("recipient_id").IsRequired();
        builder.Property(recipient => recipient.Name).HasColumnName("name").HasMaxLength(NotificationRecipient.MaxNameLength);
        builder.Property(recipient => recipient.Email).HasColumnName("email").HasMaxLength(NotificationRecipient.MaxEmailLength);

        // US-E5-2: per-recipient delivery, so a retry never re-sends the one who has it.
        builder.Property(recipient => recipient.DeliveryStatus).HasColumnName("delivery_status")
            .HasConversion<string>().HasMaxLength(20).IsRequired().HasDefaultValue(RecipientDeliveryStatus.Pending);
        builder.Property(recipient => recipient.SentAt).HasColumnName("sent_at").HasColumnType("timestamp with time zone");
        builder.Property(recipient => recipient.Attempts).HasColumnName("attempts").IsRequired();
        builder.Property(recipient => recipient.LastAttemptAt).HasColumnName("last_attempt_at").HasColumnType("timestamp with time zone");
        builder.Property(recipient => recipient.LastError).HasColumnName("last_error").HasMaxLength(NotificationRecipient.MaxErrorLength);
        builder.Property(recipient => recipient.ProviderMessageId).HasColumnName("provider_message_id")
            .HasMaxLength(NotificationRecipient.MaxProviderMessageIdLength);
        builder.Ignore(recipient => recipient.NeedsSending);
        builder.Ignore(recipient => recipient.IsStuck);

        // US-E5-3: the retry budget (reset by a manual resend).
        builder.Property(recipient => recipient.FailuresSinceReset).HasColumnName("failures_since_reset").IsRequired();

        builder.HasIndex(recipient => new { recipient.NotificationRequestId, recipient.Kind })
            .IsUnique()
            .HasDatabaseName("uq_notification_recipient_request_kind");
    }
}

/// <summary>US-E5-3 DoD 5: append-only history of every send attempt.</summary>
public sealed class NotificationAttemptConfiguration : IEntityTypeConfiguration<NotificationAttempt>
{
    public void Configure(EntityTypeBuilder<NotificationAttempt> builder)
    {
        builder.ToTable("notification_attempt", table =>
        {
            table.HasCheckConstraint("ck_notification_attempt_outcome", "outcome IN ('Sent', 'TransientFailure', 'PermanentFailure')");
            table.HasCheckConstraint("ck_notification_attempt_trigger", "attempt_trigger IN ('Automatic', 'ManualResend')");
        });

        builder.HasKey(attempt => attempt.NotificationAttemptId).HasName("pk_notification_attempt");

        builder.Property(attempt => attempt.NotificationAttemptId).HasColumnName("notification_attempt_id").ValueGeneratedNever();
        builder.Property(attempt => attempt.NotificationRequestId).HasColumnName("notification_request_id").IsRequired();
        builder.Property(attempt => attempt.NotificationRecipientId).HasColumnName("notification_recipient_id").IsRequired();
        builder.Property(attempt => attempt.CompanyId).HasColumnName("company_id").IsRequired();
        builder.Property(attempt => attempt.RecipientKind).HasColumnName("recipient_kind").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(attempt => attempt.EmailAddress).HasColumnName("email_address").HasMaxLength(NotificationRecipient.MaxEmailLength);
        builder.Property(attempt => attempt.AttemptNumber).HasColumnName("attempt_number").IsRequired();
        builder.Property(attempt => attempt.AttemptedAt).HasColumnName("attempted_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(attempt => attempt.Outcome).HasColumnName("outcome").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(attempt => attempt.ProviderResponse).HasColumnName("provider_response").HasMaxLength(NotificationAttempt.MaxTextLength);
        builder.Property(attempt => attempt.Error).HasColumnName("error").HasMaxLength(NotificationAttempt.MaxTextLength);
        builder.Property(attempt => attempt.Trigger).HasColumnName("attempt_trigger").HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(attempt => attempt.TriggeredBy).HasColumnName("triggered_by").HasMaxLength(NotificationAttempt.MaxTextLength);

        builder.HasIndex(attempt => new { attempt.NotificationRequestId, attempt.AttemptedAt })
            .HasDatabaseName("ix_notification_attempt_request_time");
    }
}
