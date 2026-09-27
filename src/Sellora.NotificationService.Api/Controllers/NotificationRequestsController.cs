using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.NotificationService.Api.Authorization;
using Sellora.NotificationService.Application.Notifications;

namespace Sellora.NotificationService.Api.Controllers;

/// <summary>
/// Read-only view of queued notification requests for the company admin, so
/// QA (US-E5-1-Q1..Q3) and support can see what an event produced without
/// database access. Scoped to the token's company by the query filter.
/// </summary>
[ApiController]
[Route("api/notification-requests")]
[Route("api/notifications")] // US-E5-3's name for the same resource
[Authorize(Policy = RolePolicies.RequireCompanyAdmin)]
public sealed class NotificationRequestsController : ControllerBase
{
    private readonly INotificationRequestReader _reader;
    private readonly INotificationResendService _resend;

    public NotificationRequestsController(INotificationRequestReader reader, INotificationResendService resend)
    {
        _reader = reader;
        _resend = resend;
    }

    /// <summary>Newest first. Filter by <c>orderId</c> or <c>orderReference</c>.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<NotificationRequestResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? orderId,
        [FromQuery] string? orderReference,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await _reader.ListAsync(
            new NotificationRequestQuery(orderId, orderReference, page, pageSize, status), cancellationToken));

    /// <summary>
    /// US-E5-3-T5: counts by delivery state for the admin dashboard. A rising
    /// <c>needsAttention</c> is a mail outage nobody would otherwise notice.
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(NotificationHealthResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Health(CancellationToken cancellationToken) =>
        Ok(await _reader.GetHealthAsync(cancellationToken));

    /// <summary>
    /// US-E5-3-T4: send again to every recipient not yet sent to, with a
    /// fresh retry budget, optionally at corrected addresses
    /// (<c>{ "recipients": [{ "kind": "Agency", "email": "…" }] }</c>).
    /// Dispatches immediately and returns the request with its new attempt.
    /// </summary>
    [HttpPost("{notificationRequestId:guid}/resend")]
    [ProducesResponseType(typeof(NotificationRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(NotificationRequestResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resend(
        Guid notificationRequestId,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] ResendNotificationRequest? body,
        CancellationToken cancellationToken)
    {
        var result = await _resend.ResendAsync(
            notificationRequestId, body ?? new ResendNotificationRequest(null), cancellationToken);

        return result.Outcome switch
        {
            ResendOutcome.Succeeded => Ok(result.Request),
            ResendOutcome.Busy => Accepted(result.Request),
            _ => ToProblem(result)
        };
    }

    private ObjectResult ToProblem(ResendResult result)
    {
        var (status, title) = result.Outcome switch
        {
            ResendOutcome.NotFound => (StatusCodes.Status404NotFound, "Notification not found"),
            ResendOutcome.InvalidRequest => (StatusCodes.Status400BadRequest, "Invalid resend"),
            ResendOutcome.NothingToResend => (StatusCodes.Status409Conflict, "Nothing to resend"),
            ResendOutcome.CallerNotPermitted => (StatusCodes.Status403Forbidden, "Caller scope missing"),
            _ => (StatusCodes.Status500InternalServerError, "Resend failed")
        };

        return StatusCode(status, new ProblemDetails { Status = status, Title = title, Detail = result.Message });
    }

    [HttpGet("{notificationRequestId:guid}")]
    [ProducesResponseType(typeof(NotificationRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid notificationRequestId, CancellationToken cancellationToken)
    {
        var request = await _reader.GetAsync(notificationRequestId, cancellationToken);

        return request is null
            ? NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Notification request not found",
                Detail = $"No notification request {notificationRequestId} is visible to you."
            })
            : Ok(request);
    }

    /// <summary>
    /// US-E5-2-T4: the exact message sent (subject, HTML, text and its
    /// SHA-256), reproduced from storage for a dispute — not re-rendered.
    /// </summary>
    [HttpGet("{notificationRequestId:guid}/rendered")]
    [ProducesResponseType(typeof(RenderedNotificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRendered(Guid notificationRequestId, CancellationToken cancellationToken)
    {
        var rendered = await _reader.GetRenderedAsync(notificationRequestId, cancellationToken);

        return rendered is null
            ? NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Rendered message not found",
                Detail = $"Notification request {notificationRequestId} is not visible to you or has not been rendered yet."
            })
            : Ok(rendered);
    }
}
