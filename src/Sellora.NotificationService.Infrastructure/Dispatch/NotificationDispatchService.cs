using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.NotificationService.Application.Dispatch;

namespace Sellora.NotificationService.Infrastructure.Dispatch;

/// <summary>
/// Runs a dispatch pass every PollIntervalSeconds (immediately again while
/// there was work). A failing pass — database or SMTP down — is logged and
/// retried on the next tick; it never stops the host or the Kafka consumer.
/// </summary>
public sealed class NotificationDispatchService(
    IServiceScopeFactory scopeFactory,
    IOptions<DispatchOptions> options,
    ILogger<NotificationDispatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        var settings = options.Value;

        if (!settings.Enabled)
        {
            logger.LogInformation("Notification dispatch is disabled (Dispatch:Enabled = false).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.PollIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            var dispatched = 0;

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                dispatched = await scope.ServiceProvider.GetRequiredService<INotificationDispatcher>()
                    .DispatchDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Notification dispatch pass failed; retrying in {Interval}.", interval);
            }

            if (dispatched == 0)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
