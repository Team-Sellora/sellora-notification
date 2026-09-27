namespace Sellora.NotificationService.Domain.Notifications;

/// <summary>
/// US-E5-3-T2: the shape an address must have before it is worth sending
/// to. Deliberately simple — one @, something on each side, a dot in the
/// domain, no spaces — because the provider is the real judge; this only
/// catches the malformed addresses that would be rejected every time.
/// </summary>
public static class EmailAddressRules
{
    public static bool IsWellFormed(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var value = address.Trim();

        if (value.Length > 320 || value.Any(char.IsWhiteSpace) || value.Count(c => c == '@') != 1)
        {
            return false;
        }

        var at = value.IndexOf('@');
        var local = value[..at];
        var domain = value[(at + 1)..];

        return local.Length > 0 &&
               domain.Length > 2 &&
               domain.Contains('.') &&
               !domain.StartsWith('.') &&
               !domain.EndsWith('.') &&
               !domain.Contains("..");
    }
}
