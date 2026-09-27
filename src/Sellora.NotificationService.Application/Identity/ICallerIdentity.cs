namespace Sellora.NotificationService.Application.Identity;

/// <summary>Who is calling, from the access token — recorded on a manual resend.</summary>
public interface ICallerIdentity
{
    /// <summary>The token's <c>sub</c> claim.</summary>
    string? UserId { get; }
}
