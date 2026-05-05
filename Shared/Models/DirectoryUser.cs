namespace Shared.Models;

// Detailed view of a user entry in the directory used when populating dialogs
// that need more than the list-level DirectoryItem.
public sealed record DirectoryUser(string Dn, string Name, string? Mail, string? Username);
