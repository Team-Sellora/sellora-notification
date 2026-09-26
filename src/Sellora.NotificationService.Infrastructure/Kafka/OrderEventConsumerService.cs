using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.NotificationService.Application.Notifications;

namespace Sellora.NotificationService.Infrastructure.Kafka;

/// <summary>
/// US-E5-1-T2 / T4: consumes <c>sellora.order.v1</c> one record at a time.
///
/// The offset is committed only after the record is dealt with — stored as a
/// request, recognised as a duplicate or as non-notifiable, or acknowledged
/// by the dead-letter topic. Anything else (database down, dead-letter topic
/// unreachable) leaves the offset uncommitted: the consumer rejoins and the
/// record is replayed, so a crash between consuming and storing loses
/// nothing, and the unique event ID stops the replay creating a second row.
///
/// A malformed or unknown message is parked on the dead-letter topic with
/// its raw payload and then committed, so it cannot block the records behind
/// it on the same partition. Same loop shape as sellora-inventory's
/// OrderEventConsumerService.
/// </summary>
public sealed class OrderEventConsumerService(
    IOptions<OrderEventConsumerOptions> options,
    IServiceScopeFactory scopeFactory,
    ILogger<OrderEventConsumerService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting; Consume blocks.
        await Task.Yield();

        var settings = options.Value;
        Validate(settings);

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = settings.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = 10_000
        };
        KafkaSaslConfigurator.Apply(producerConfig, settings.SaslUsername, settings.SaslPassword);

        using var deadLetterProducer = new ProducerBuilder<string, string>(producerConfig).Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            var consumerConfig = new ConsumerConfig
            {
                BootstrapServers = settings.BootstrapServers,
                GroupId = settings.ConsumerGroupId,
                AutoOffsetReset = Enum.Parse<AutoOffsetReset>(settings.AutoOffsetReset, ignoreCase: true),
                EnableAutoCommit = false,
                EnableAutoOffsetStore = false
            };
            KafkaSaslConfigurator.Apply(consumerConfig, settings.SaslUsername, settings.SaslPassword);

            using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();

            try
            {
                consumer.Subscribe(settings.OrderTopic);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var record = consumer.Consume(stoppingToken);

                    if (record is null || record.IsPartitionEOF)
                    {
                        continue;
                    }

                    await HandleAsync(record, settings, deadLetterProducer, stoppingToken);

                    // Only reached once the record is safely dealt with.
                    consumer.Commit(record);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Notification consumer interrupted; rejoining from committed offsets. The failed record was not committed and will be replayed.");
            }
            finally
            {
                try
                {
                    consumer.Close();
                }
                catch (KafkaException exception)
                {
                    logger.LogWarning(exception, "Kafka consumer could not close cleanly.");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(settings.RetryDelayMilliseconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task HandleAsync(
        ConsumeResult<string, string> record,
        OrderEventConsumerOptions settings,
        IProducer<string, string> deadLetterProducer,
        CancellationToken cancellationToken)
    {
        var message = new ConsumedMessage(
            record.Topic, record.Partition.Value, record.Offset.Value, record.Message.Key, record.Message.Value);

        // A fresh scope (and DbContext) per record, so a failed save cannot
        // leave tracked state behind for the replay.
        await using var scope = scopeFactory.CreateAsyncScope();
        var intake = scope.ServiceProvider.GetRequiredService<INotificationIntake>();

        var result = await intake.AcceptAsync(message, cancellationToken);

        switch (result.Outcome)
        {
            case IntakeOutcome.DeadLetter:
                // Awaited: if the dead-letter topic cannot be reached this
                // throws, the offset stays uncommitted and the record replays.
                await deadLetterProducer.ProduceAsync(
                    settings.DeadLetterTopic,
                    DeadLetterMessage(record, result.Reason),
                    cancellationToken);

                logger.LogError(
                    "NotificationEventDeadLettered {Topic}/{Partition}/{Offset} sent to {DeadLetterTopic}: {Reason}",
                    record.Topic, record.Partition.Value, record.Offset.Value, settings.DeadLetterTopic, result.Reason);
                break;

            case IntakeOutcome.Ignored:
                logger.LogDebug(
                    "Ignored {Topic}/{Partition}/{Offset}: {Reason}",
                    record.Topic, record.Partition.Value, record.Offset.Value, result.Reason);
                break;
        }
    }

    /// <summary>
    /// Same envelope as sellora-inventory's dead-letter topic: the raw payload
    /// untouched, where it came from, and why it was parked.
    /// </summary>
    internal static Message<string, string> DeadLetterMessage(ConsumeResult<string, string> record, string reason) => new()
    {
        Key = $"{record.Topic}:{record.Partition.Value}:{record.Offset.Value}",
        Value = JsonSerializer.Serialize(
            new
            {
                sourceTopic = record.Topic,
                sourcePartition = record.Partition.Value,
                sourceOffset = record.Offset.Value,
                messageKey = record.Message.Key,
                payload = record.Message.Value,
                headers = record.Message.Headers?.Select(header => new
                {
                    key = header.Key,
                    value = header.GetValueBytes() is { } bytes ? Convert.ToBase64String(bytes) : null
                }),
                reason,
                failedAt = DateTimeOffset.UtcNow
            },
            Json),
        Headers = new Headers
        {
            { "dead-letter-reason", Encoding.UTF8.GetBytes(reason.Length > 500 ? reason[..500] : reason) }
        }
    };

    private static void Validate(OrderEventConsumerOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BootstrapServers) ||
            string.IsNullOrWhiteSpace(settings.OrderTopic) ||
            string.IsNullOrWhiteSpace(settings.DeadLetterTopic) ||
            string.IsNullOrWhiteSpace(settings.ConsumerGroupId) ||
            settings.OrderTopic == settings.DeadLetterTopic ||
            !Enum.TryParse<AutoOffsetReset>(settings.AutoOffsetReset, ignoreCase: true, out _))
        {
            throw new InvalidOperationException(
                "Kafka BootstrapServers, OrderTopic, DeadLetterTopic (distinct) and ConsumerGroupId must be configured, and AutoOffsetReset must be Earliest or Latest.");
        }
    }
}
