using System.Collections.Concurrent;
using Sellora.NotificationService.Application.Dispatch;

namespace Sellora.NotificationService.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public DateTimeOffset Now
    {
        get => _now;
        set => _now = value;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Records every email instead of sending it. Can fail chosen addresses, and
/// can require N sends to be in flight at the same moment (proves the sends
/// are concurrent: sequential sends would never meet at the gate).
/// </summary>
internal sealed class RecordingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<OutgoingEmail> _sent = new();
    private TaskCompletionSource? _gate;
    private int _arrivals;
    private int _gateSize;

    public IReadOnlyCollection<OutgoingEmail> Sent => _sent.ToArray();

    /// <summary>Addresses that currently reject mail.</summary>
    public HashSet<string> Unreachable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void RequireConcurrent(int count)
    {
        _gateSize = count;
        _arrivals = 0;
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task<string?> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        if (_gate is { } gate)
        {
            if (Interlocked.Increment(ref _arrivals) >= _gateSize)
            {
                gate.TrySetResult();
            }

            // Sequential sending would wait here forever; concurrent sending passes.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        if (Unreachable.Contains(email.ToAddress))
        {
            throw new InvalidOperationException($"Mailbox unavailable: {email.ToAddress}");
        }

        _sent.Enqueue(email);
        return $"queued-{Guid.NewGuid():N}";
    }
}
