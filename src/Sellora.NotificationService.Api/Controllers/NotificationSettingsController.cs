using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sellora.NotificationService.Api.Authorization;
using Sellora.NotificationService.Application.Notifications;

namespace Sellora.NotificationService.Api.Controllers;

/// <summary>
/// US-E5-4: where the company's alerts (low stock) are sent. Under
/// /api/notifications so the existing APIM resources cover it.
/// </summary>
[ApiController]
[Route("api/notifications/settings")]
[Authorize(Policy = RolePolicies.RequireCompanyAdmin)]
public sealed class NotificationSettingsController(INotificationSettingsService settings) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(NotificationSettingsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await settings.GetAsync(cancellationToken));

    /// <summary><c>{ "alertEmail": "ops@company.lk" }</c>; null or blank clears it.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(NotificationSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Put(UpdateNotificationSettingsRequest body, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await settings.UpdateAsync(body, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid alert address",
                Detail = exception.Message.Split(" (Parameter")[0]
            });
        }
    }
}
