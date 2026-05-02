namespace Shared.Models;

// Lightweight representation of a directory entry returned to the UI layer.
// Name is the formatted, human-readable label (e.g. "Doe, John (jdoe)" or a group's CN).
public sealed record DirectoryItem(string Dn, string Name);
