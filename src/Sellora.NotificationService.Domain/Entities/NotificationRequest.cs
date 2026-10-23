using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Domain.Tenancy;

namespace Sellora.NotificationService.Domain.Entities;

/// <summary>
/// US-E5-1: one notification to send, created from exactly one consumed
/// event. <see cref="SourceEventId"/> is unique, so a Kafka redelivery of
/// the same event can never create a second request. The raw event is kept
/// so the message can be composed (US-E5-2) from what the order service
/// actually said at the time, not a later re-read that may have changed.
/// </summary>
public sealed class NotificationRequest : ITenantScoped
{
    public const int MaxReferenceLength = 50;

    private readonly List<NotificationRecipient> _recipients = new();
    private readonly List<NotificationAttempt> _attempts = new();

    private NotificationRequest()
    {
    }

    public Guid NotificationRequestId { get; private set; }

    public Guid CompanyId { get; private set; }

    /// <summary>The event's own ID — the idempotency key.</summary>
    public Guid SourceEventId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    /// <summary>Which message to compose, e.g. <c>payment-recorded.v1</c>.</summary>
    public string TemplateKey { get; private set; } = string.Empty;

    /// <summary>The order the event is about; null for events about stock (US-E5-4).</summary>
    public Guid? OrderId { get; private set; }

    /// <summary>
    /// The reference a person quotes: the order reference (ORD-260918-K7MQ4R)
    /// or, for low stock, a stock reference (STOCK-260929-1A2B3C).
    /// </summary>
    public string OrderReference { get; private set; } = string.Empty;

    /// <summary>
    /// US-E5-4: details looked up when the request was stored (names the raw
    /// event does not carry, e.g. the product name for low stock), as JSON.
    /// Kept apart from <see cref="Payload"/>, which stays exactly as consumed.
    /// </summary>
    public string? Context { get; private set; }

    public NotificationStatus Status { get; private set; }

    /// <summary>The event exactly as consumed (JSON).</summary>
    public string Payload { get; private set; } = string.Empty;

    public string? CorrelationId { get; private set; }

    /// <summary>When the business event happened (the event's occurredAt).</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>When this service stored it.</summary>
    public DateTimeOffset ReceivedAt { get; private set; }

    public string SourceTopic { get; private set; } = string.Empty;

    public int SourcePartition { get; private set; }

    public long SourceOffset { get; private set; }

    public IReadOnlyCollection<NotificationRecipient> Recipients => _recipients.AsReadOnly();

    // ── US-E5-2: the rendered message and its dispatch ───────────────────

    /// <summary>The subject exactly as sent (includes the order reference).</summary>
    public string? RenderedSubject { get; private set; }

    /// <summary>The HTML body exactly as sent — kept for dispute reproduction.</summary>
    public string? RenderedHtml { get; private set; }

    /// <summary>The plain-text alternative exactly as sent.</summary>
    public string? RenderedText { get; private set; }

    /// <summary>SHA-256 over subject + HTML + text; also sent as a header, so a forwarded copy can be matched.</summary>
    public string? RenderedBodySha256 { get; private set; }

    public DateTimeOffset? RenderedAt { get; private set; }

    public int AttemptCount { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>When a retry is due; null when nothing is left that can be sent.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    /// <summary>When every recipient had received it.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// Gap in milliseconds between the first and the last recipient's send
    /// timestamps — the measured "simultaneous". Set once all addressed
    /// recipients have been sent to.
    /// </summary>
    public int? SendGapMilliseconds { get; private set; }

    /// <summary>A dispatcher's claim on this row; others skip it until it expires.</summary>
    public DateTimeOffset? ClaimedUntil { get; private set; }

    public bool IsRendered => RenderedAt is not null;

    /// <summary>
    /// Stores the one rendered message. Rendering happens once per request:
    /// a retry sends the stored message, never a re-render, so the two
    /// recipients cannot end up holding different text.
    /// </summary>
    public void AttachRendering(RenderedMessage message, DateTimeOffset renderedAt)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (IsRendered)
        {
            throw new InvalidOperationException(
                $"Notification request {NotificationRequestId} is already rendered; it is never rendered twice.");
        }

        RenderedSubject = message.Subject;
        RenderedHtml = message.Html;
        RenderedText = message.Text;
        RenderedBodySha256 = message.BodySha256;
        RenderedAt = renderedAt;
    }

    public RenderedMessage? Rendered =>
        IsRendered ? new RenderedMessage(RenderedSubject!, RenderedHtml!, RenderedText!, RenderedBodySha256!) : null;

    /// <summary>Recipients with no address are marked so they are reported, not retried forever.</summary>
    public void MarkUnaddressedRecipients()
    {
        foreach (var recipient in _recipients)
        {
            recipient.MarkUnaddressed();
        }
    }

    /// <summary>Who is still owed the message: never those already sent to.</summary>
    public IReadOnlyList<NotificationRecipient> RecipientsToSend() =>
        _recipients.Where(recipient => recipient.NeedsSending).ToList();

    /// <summary>US-E5-3: every attempt to every recipient, oldest first.</summary>
    public IReadOnlyCollection<NotificationAttempt> AttemptHistory => _attempts.AsReadOnly();

    /// <summary>When an admin last pressed Resend, and who.</summary>
    public DateTimeOffset? LastResendAt { get; private set; }

    public string? LastResendBy { get; private set; }

    /// <summary>The failure an admin should read first: the most recent error of a recipient not yet sent to.</summary>
    public string? FailureReason =>
        _recipients
            .Where(recipient => recipient.DeliveryStatus != RecipientDeliveryStatus.Sent && recipient.LastError is not null)
            .OrderByDescending(recipient => recipient.LastAttemptAt ?? DateTimeOffset.MinValue)
            .Select(recipient => recipient.LastError)
            .FirstOrDefault();

    /// <summary>
    /// Records one dispatch attempt and sets the request status:
    ///   everyone has it                    → Sent
    ///   someone can still be retried       → PartiallySent (some have it) or Failed (nobody yet),
    ///                                        next attempt after the back-off
    ///   nobody left who can be retried     → PermanentlyFailed (admin resend only)
    /// </summary>
    public void RecordDispatch(
        IReadOnlyCollection<RecipientSendResult> results,
        DateTimeOffset completedAt,
        RetryPolicy policy,
        AttemptTrigger trigger = AttemptTrigger.Automatic,
        string? triggeredBy = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(policy);

        foreach (var result in results)
        {
            var recipient = _recipients.SingleOrDefault(
                candidate => candidate.NotificationRecipientId == result.NotificationRecipientId)
                ?? throw new InvalidOperationException(
                    $"Recipient {result.NotificationRecipientId} is not on request {NotificationRequestId}.");

            var attemptNumber = recipient.Apply(result, policy.MaxAttempts);

            if (attemptNumber is { } number)
            {
                _attempts.Add(new NotificationAttempt(recipient, number, result, trigger, triggeredBy));
            }
        }

        AttemptCount += 1;
        LastAttemptAt = completedAt;
        ClaimedUntil = null;

        var sentCount = _recipients.Count(recipient => recipient.DeliveryStatus == RecipientDeliveryStatus.Sent);
        var retryable = _recipients.Where(recipient => recipient.NeedsSending).ToList();

        if (sentCount == _recipients.Count)
        {
            Status = NotificationStatus.Sent;
            CompletedAt ??= completedAt;
            NextAttemptAt = null;
        }
        else if (retryable.Count > 0)
        {
            Status = sentCount > 0 ? NotificationStatus.PartiallySent : NotificationStatus.Failed;
            var failuresSoFar = retryable.Max(recipient => recipient.FailuresSinceReset);
            NextAttemptAt = completedAt + policy.Delay(Math.Max(1, failuresSoFar));
        }
        else
        {
            Status = NotificationStatus.PermanentlyFailed;
            NextAttemptAt = null;
        }

        var addressed = _recipients.Where(recipient => recipient.Email is not null).ToList();

        if (addressed.Count >= 2 && addressed.All(recipient => recipient.SentAt is not null))
        {
            var first = addressed.Min(recipient => recipient.SentAt!.Value);
            var last = addressed.Max(recipient => recipient.SentAt!.Value);
            SendGapMilliseconds = (int)Math.Min(int.MaxValue, Math.Round((last - first).TotalMilliseconds));
        }
    }

    /// <summary>
    /// US-E5-3-T4: a company admin asks for another try. Every recipient not
    /// yet sent to goes back in the queue with a fresh retry budget, at a
    /// corrected address when one is given. Nothing already sent is touched,
    /// and the attempt history is kept.
    /// </summary>
    /// <param name="correctedEmails">Per recipient kind, a replacement address (already validated by the caller).</param>
    public void PrepareResend(
        IReadOnlyDictionary<RecipientKind, string> correctedEmails,
        string requestedBy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(correctedEmails);

        if (Status == NotificationStatus.Sent)
        {
            throw new InvalidOperationException("Every recipient already has this notification; there is nothing to resend.");
        }

        // Check before changing anything, so a refused resend leaves the request as it was.
        var sendable = _recipients.Any(recipient =>
            recipient.DeliveryStatus != RecipientDeliveryStatus.Sent &&
            (correctedEmails.ContainsKey(recipient.Kind) || recipient.Email is not null));

        if (!sendable)
        {
            throw new InvalidOperationException(
                "No recipient has an email address to send to. Give a corrected address to resend.");
        }

        foreach (var recipient in _recipients)
        {
            recipient.ResetForResend(correctedEmails.GetValueOrDefault(recipient.Kind));
        }

        LastResendAt = now;
        LastResendBy = requestedBy.Trim();
        NextAttemptAt = now;
        Status = _recipients.Any(recipient => recipient.DeliveryStatus == RecipientDeliveryStatus.Sent)
            ? NotificationStatus.PartiallySent
            : NotificationStatus.Pending;
    }

    public static NotificationRequest CreatePending(
        Guid companyId,
        Guid sourceEventId,
        string eventType,
        string templateKey,
        Guid? orderId,
        string orderReference,
        string payload,
        string? correlationId,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        EventSource source,
        IReadOnlyCollection<NewRecipient> recipients,
        string? context = null)
    {
        Require(companyId != Guid.Empty, nameof(companyId));
        Require(sourceEventId != Guid.Empty, nameof(sourceEventId));
        Require(!string.IsNullOrWhiteSpace(eventType), nameof(eventType));
        Require(!string.IsNullOrWhiteSpace(templateKey), nameof(templateKey));
        Require(orderId != Guid.Empty, nameof(orderId));
        Require(!string.IsNullOrWhiteSpace(orderReference) && orderReference.Length <= MaxReferenceLength, nameof(orderReference));
        Require(!string.IsNullOrWhiteSpace(payload), nameof(payload));
        Require(recipients is { Count: > 0 }, nameof(recipients));
        Require(recipients.Select(recipient => recipient.Kind).Distinct().Count() == recipients.Count, "recipients (one per kind)");

        var request = new NotificationRequest
        {
            NotificationRequestId = Guid.NewGuid(),
            CompanyId = companyId,
            SourceEventId = sourceEventId,
            EventType = eventType,
            TemplateKey = templateKey,
            OrderId = orderId,
            OrderReference = orderReference,
            Context = string.IsNullOrWhiteSpace(context) ? null : context,
            Status = NotificationStatus.Pending,
            Payload = payload,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId,
            OccurredAt = occurredAt,
            ReceivedAt = receivedAt,
            SourceTopic = source.Topic,
            SourcePartition = source.Partition,
            SourceOffset = source.Offset
        };

        foreach (var recipient in recipients)
        {
            request._recipients.Add(new NotificationRecipient(request.NotificationRequestId, companyId, recipient));
        }

        return request;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new ArgumentException($"A notification request needs a valid {name}.", name);
        }
    }
}

/// <summary>Where a request came from in Kafka, for tracing it back to its record.</summary>
public sealed record EventSource(string Topic, int Partition, long Offset);

/// <summary>A recipient derived from the event.</summary>
/// <param name="Email">Null when Organization had no email for them; kept so the gap is visible (US-E5-3).</param>
public sealed record NewRecipient(RecipientKind Kind, Guid RecipientId, string? Name, string? Email);
