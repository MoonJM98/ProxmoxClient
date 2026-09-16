namespace ProxmoxClient.Core.Models;

/// <summary>
///     A VM/CT snapshot. The pseudo-entry "!current" exposed by the API is filtered
///     out by the client, so every item is a real, rollback-able snapshot.
/// </summary>
public sealed class PveSnapshot
{
    /// <summary>Snapshot name (unique per guest).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Description text, when set.</summary>
    public string? Description { get; init; }

    /// <summary>Creation time (UTC); null for snapshots without a timestamp.</summary>
    public DateTime? SnapTimeUtc { get; init; }

    /// <summary>True when the snapshot also contains the RAM state ("vmstate").</summary>
    public bool HasRam { get; init; }

    /// <summary>Name of the parent snapshot in the tree, when reported.</summary>
    public string? Parent { get; init; }
}