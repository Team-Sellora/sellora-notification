namespace Sellora.NotificationService.Infrastructure.Kafka;

/// <summary>Bound to the <c>Kafka</c> section (App Service: <c>Kafka__BootstrapServers</c>, …).</summary>
public sealed class OrderEventConsumerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; init; } = "localhost:9092";

    /// <summary>
    /// The topic sellora-order publishes to. The backlog calls it
    /// <c>order.events</c>; the real name (sellora-order KafkaOptions,
    /// Inventory's consumer) is <c>sellora.order.v1</c>.
    /// </summary>
    public string OrderTopic { get; init; } = "sellora.order.v1";

    /// <summary>
    /// US-E5-4: where sellora-inventory publishes LowStockDetected. Empty
    /// disables it. Shared topic: unknown event types there are ignored.
    /// </summary>
    public string InventoryTopic { get; init; } = "sellora.inventory.v1";

    /// <summary>
    /// US-E5-4: where Delivery & Returns (E6) will publish DeliveryStatusChanged
    /// and DeliveryDisputed. Until that topic exists the consumer logs a
    /// warning and keeps processing the others.
    /// </summary>
    public string DeliveryTopic { get; init; } = "sellora.delivery.v1";

    public string DeadLetterTopic { get; init; } = "sellora.notification.dead-letter.v1";

    /// <summary>Every topic consumed, order topic first; blanks skipped.</summary>
    public IReadOnlyList<string> Topics() =>
        new[] { OrderTopic, InventoryTopic, DeliveryTopic }
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public string ConsumerGroupId { get; init; } = "sellora.notification.order.v1";

    /// <summary>
    /// Where a brand-new consumer group starts. "Latest" by default so the
    /// first deploy does not queue a notification for every historical order
    /// on the topic; after that, committed offsets decide. Tests use "Earliest".
    /// </summary>
    public string AutoOffsetReset { get; init; } = "Latest";

    /// <summary>Wait before rejoining after a failure (e.g. the database is down).</summary>
    public int RetryDelayMilliseconds { get; init; } = 5_000;

    public string? SaslUsername { get; init; }

    public string? SaslPassword { get; init; }
}
