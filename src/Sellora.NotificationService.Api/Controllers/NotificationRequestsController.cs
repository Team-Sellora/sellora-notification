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
[Authorize(Policy = RolePolicies.RequireCompanyAdmin)]
public sealed class NotificationRequestsController : ControllerBase
{
    private readonly INotificationRequestReader _reader;

    public NotificationRequestsController(INotificationRequestReader reader)
    {
        _reader = reader;
    }

    /// <summary>Newest first. Filter by <c>orderId</c> or <c>orderReference</c>.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<NotificationRequestResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? orderId,
        [FromQuery] string? orderReference,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default) =>
        Ok(await _reader.ListAsync(new NotificationRequestQuery(orderId, orderReference, page, pageSize), cancellationToken));

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
}
