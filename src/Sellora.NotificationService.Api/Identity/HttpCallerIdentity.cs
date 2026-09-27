using Sellora.NotificationService.Application.Identity;

namespace Sellora.NotificationService.Api.Identity;

/// <summary>Reads the caller from the validated JWT (MapInboundClaims is off, so "sub" arrives as "sub").</summary>
public sealed class HttpCallerIdentity(IHttpContextAccessor accessor) : ICallerIdentity
{
    public string? UserId => accessor.HttpContext?.User.FindFirst("sub")?.Value;
}
