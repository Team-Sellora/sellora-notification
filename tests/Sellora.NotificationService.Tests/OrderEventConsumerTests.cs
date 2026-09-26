using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Domain.Tenancy;
using Sellora.NotificationService.Infrastructure.Kafka;
using Sellora.NotificationService.Infrastructure.Notifications;
using Sellora.NotificationService.Infrastructure.Persistence;
using Testcontainers.Kafka;

namespace Sellora.NotificationService.Tests;

/// <summary>
/// US-E5-1-T2 / T4 and QA Q2–Q4 against a real broker (Testcontainers):
/// offsets move only after the request is stored, a crash in between
/// replays, redelivery never duplicates, and a poison message is parked on
/// the dead-letter topic without holding up the partition.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class OrderEventConsumerTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly PostgreSqlFixture _fixture;
    private readonly KafkaContainer _kafka = new KafkaBuilder("confluentinc/cp-kafka:7.6.1").Build();
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly string _topic = $"sellora.order.test.{Guid.NewGuid():N}";
    private readonly string _deadLetterTopic = $"sellora.notification.dlt.test.{Guid.NewGuid():N}";

    public OrderEventConsumerTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _kafka.StartAsync();

        // One partition, so "the next message on the same partition" is literal.
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _kafka.GetBootstrapAddress() }).Build();
        await admin.CreateTopicsAsync(new[]
        {
            new TopicSpecification { Name = _topic, NumPartitions = 1, ReplicationFactor = 1 },
            new TopicSpecification { Name = _deadLetterTopic, NumPartitions = 1, ReplicationFactor = 1 }
        });
    }

    public Task DisposeAsync() => _kafka.DisposeAsync().AsTask();

    // Q4
    [Fact]
    public async Task A_poison_message_is_dead_lettered_with_its_raw_payload_and_the_next_message_still_processes()
    {
        const string poison = "{\"eventType\": \"OrderPlaced\", this is not json";
        var good = TestEvents.Order("PaymentRecorded", companyId: _companyId);

        await ProduceAsync(poison);
        await ProduceAsync(good.ToJsonString());

        await RunConsumerUntilAsync(async () => await CountAsync(TestEvents.EventId(good)) == 1);

        var parked = await ReadDeadLetterAsync();
        using var envelope = JsonDocument.Parse(parked);
        Assert.Equal(poison, envelope.RootElement.GetProperty("payload").GetString());
        Assert.Equal(_topic, envelope.RootElement.GetProperty("sourceTopic").GetString());
        Assert.Equal(0, envelope.RootElement.GetProperty("sourceOffset").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(envelope.RootElement.GetProperty("reason").GetString()));
    }

    // Q1 through Kafka: one of each type, one request each.
    [Fact]
    public async Task One_of_each_event_type_produces_one_request_each()
    {
        var events = new[] { "OrderPlaced", "OrderConfirmed", "PaymentRecorded", "OrderCancelled" }
            .Select(type => TestEvents.Order(type, companyId: _companyId))
            .ToList();

        foreach (var @event in events)
        {
            await ProduceAsync(@event.ToJsonString());
        }

        await RunConsumerUntilAsync(async () =>
        {
            foreach (var @event in events)
            {
                if (await CountAsync(TestEvents.EventId(@event)) != 1) return false;
            }

            return true;
        });
    }

    // Q2
    [Fact]
    public async Task The_same_event_published_twice_stores_one_request()
    {
        var @event = TestEvents.Order("OrderConfirmed", companyId: _companyId);
        var marker = TestEvents.Order("OrderPlaced", companyId: _companyId);

        await ProduceAsync(@event.ToJsonString());
        await ProduceAsync(@event.ToJsonString());
        await ProduceAsync(marker.ToJsonString()); // processed after both copies

        await RunConsumerUntilAsync(async () => await CountAsync(TestEvents.EventId(marker)) == 1);

        Assert.Equal(1, await CountAsync(TestEvents.EventId(@event)));
    }

    // Q3, first half: crash after consuming, before storing → replayed, stored once.
    [Fact]
    public async Task A_crash_before_the_request_is_stored_replays_the_event()
    {
        var @event = TestEvents.Order("PaymentRecorded", companyId: _companyId);
        await ProduceAsync(@event.ToJsonString());

        var crashes = new CrashOnce(storeFirst: false);

        await RunConsumerUntilAsync(async () => await CountAsync(TestEvents.EventId(@event)) == 1, crashes);

        Assert.Equal(1, crashes.Crashes);
        Assert.True(crashes.Attempts >= 2, "The event was not replayed after the crash.");
        Assert.Equal(1, await CountAsync(TestEvents.EventId(@event)));
    }

    // Q3, second half: stored, then crash before the offset commit → replayed, still one row.
    [Fact]
    public async Task A_crash_after_storing_but_before_committing_does_not_duplicate()
    {
        var @event = TestEvents.Order("OrderCancelled", companyId: _companyId);
        var marker = TestEvents.Order("OrderPlaced", companyId: _companyId);
        await ProduceAsync(@event.ToJsonString());
        await ProduceAsync(marker.ToJsonString());

        var crashes = new CrashOnce(storeFirst: true);

        await RunConsumerUntilAsync(async () => await CountAsync(TestEvents.EventId(marker)) == 1, crashes);

        Assert.Equal(1, crashes.Crashes);
        Assert.Equal(1, await CountAsync(TestEvents.EventId(@event)));
    }

    private async Task ProduceAsync(string value)
    {
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig { BootstrapServers = _kafka.GetBootstrapAddress(), Acks = Acks.All }).Build();

        await producer.ProduceAsync(_topic, new Message<string, string> { Key = "ORD-260926-K7MQ4R", Value = value });
    }

    private async Task<int> CountAsync(string eventId)
    {
        await using var db = _fixture.CreateContext(null);
        var id = Guid.Parse(eventId);
        return await db.NotificationRequests.IgnoreQueryFilters().CountAsync(request => request.SourceEventId == id);
    }

    private async Task RunConsumerUntilAsync(Func<Task<bool>> done, CrashOnce? crashes = null)
    {
        var services = new ServiceCollection()
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .AddSingleton(TimeProvider.System)
            .AddScoped<ITenantContext>(_ => new TenantStub(null))
            .AddDbContext<NotificationDbContext>(options => options.UseNpgsql(_fixture.ConnectionString))
            .AddScoped<NotificationIntakeService>()
            .AddScoped<INotificationIntake>(provider => crashes is null
                ? provider.GetRequiredService<NotificationIntakeService>()
                : crashes.Wrap(provider.GetRequiredService<NotificationIntakeService>()))
            .BuildServiceProvider();

        var consumer = new OrderEventConsumerService(
            Options.Create(new OrderEventConsumerOptions
            {
                BootstrapServers = _kafka.GetBootstrapAddress(),
                OrderTopic = _topic,
                DeadLetterTopic = _deadLetterTopic,
                ConsumerGroupId = $"notification-test-{Guid.NewGuid():N}",
                AutoOffsetReset = "Earliest",
                RetryDelayMilliseconds = 200
            }),
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OrderEventConsumerService>.Instance);

        await consumer.StartAsync(CancellationToken.None);

        try
        {
            var deadline = DateTime.UtcNow + Patience;

            while (!await done())
            {
                Assert.True(DateTime.UtcNow < deadline, "The consumer did not reach the expected state in time.");
                await Task.Delay(250);
            }
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
            await services.DisposeAsync();
        }
    }

    private async Task<string> ReadDeadLetterAsync()
    {
        using var reader = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _kafka.GetBootstrapAddress(),
            GroupId = $"dlt-reader-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();

        reader.Subscribe(_deadLetterTopic);
        var record = reader.Consume(Patience) ?? throw new Xunit.Sdk.XunitException("Nothing reached the dead-letter topic.");
        reader.Close();
        return record.Message.Value;
    }

    /// <summary>Simulates the process dying once, either before or right after storing.</summary>
    private sealed class CrashOnce(bool storeFirst)
    {
        private int _crashes;
        private int _attempts;

        public bool StoreFirst { get; } = storeFirst;

        public int Crashes => _crashes;

        public int Attempts => _attempts;

        public INotificationIntake Wrap(INotificationIntake inner) => new Intake(this, inner);

        private sealed class Intake(CrashOnce owner, INotificationIntake inner) : INotificationIntake
        {
            public async Task<IntakeResult> AcceptAsync(ConsumedMessage message, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._attempts);
                var crashNow = Volatile.Read(ref owner._crashes) == 0;

                if (crashNow && !owner.StoreFirst)
                {
                    Interlocked.Increment(ref owner._crashes);
                    throw new InvalidOperationException("Simulated crash before storing.");
                }

                var result = await inner.AcceptAsync(message, cancellationToken);

                if (crashNow && owner.StoreFirst)
                {
                    Interlocked.Increment(ref owner._crashes);
                    throw new InvalidOperationException("Simulated crash after storing, before the commit.");
                }

                return result;
            }
        }
    }
}
