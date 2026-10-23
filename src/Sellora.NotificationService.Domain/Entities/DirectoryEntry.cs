using Sellora.NotificationService.Domain.Tenancy;

namespace Sellora.NotificationService.Domain.Entities;

/// <summary>What a directory entry describes. Stored as text.</summary>
public enum DirectoryEntryKind
{
    Agency = 1,
    Shop = 2,
    Product = 3
}

/// <summary>
/// US-E5-4: names and addresses this service has learned from the order
/// events it already consumes (every order carries its shop, agency and
/// product names, and the shop's and agency's emails). Low-stock events carry
/// only IDs, so this is how they get a readable product name and the agency's
/// address without calling another service from a background consumer.
/// Latest non-empty value wins.
/// </summary>
public sealed class DirectoryEntry : ITenantScoped
{
    public Guid CompanyId { get; set; }

    public DirectoryEntryKind Kind { get; set; }

    /// <summary>The agency, shop or product ID.</summary>
    public Guid EntryId { get; set; }

    public string? Name { get; set; }

    public string? Email { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
