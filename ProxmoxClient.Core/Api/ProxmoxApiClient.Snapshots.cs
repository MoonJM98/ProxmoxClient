using System.Text.Json.Serialization;
using ProxmoxClient.Core.Localization;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Api;

/// <summary>게스트 스냅숏 — 목록·만들기·되돌리기·삭제.</summary>
public sealed partial class ProxmoxApiClient
{
    /// <summary>
    ///     Lists a guest's snapshots (GET .../snapshot), excluding the "!current"
    ///     pseudo-entry.
    /// </summary>
    [Versioning.PveApi("GET", "/nodes/{node}/qemu/{vmid}/snapshot")]
    [Versioning.PveApi("GET", "/nodes/{node}/lxc/{vmid}/snapshot")]
    public Task<IReadOnlyList<PveSnapshot>> GetSnapshotsAsync(string node, ResourceKind kind, int vmid,
        CancellationToken ct = default)
    {
        return GetListAsync<PveSnapshot, SnapshotDto>(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/snapshot",
            MapSnapshot,
            ct);
    }
    /// <summary>
    ///     Creates a snapshot (POST .../snapshot, form: vmid, name, description,
    ///     vmstate=1 when <paramref name="includeRam" />). Returns the task UPID.
    /// </summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/snapshot")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/snapshot")]
    public Task<string> CreateSnapshotAsync(
        string node,
        ResourceKind kind,
        int vmid,
        string name,
        string? description = null,
        bool includeRam = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ProxmoxApiException(0, Res.T("ProxmoxApiClient_04"));

        var form = new Dictionary<string, string>
        {
            ["vmid"] = vmid.ToString(),
            ["name"] = name
        };
        if (!string.IsNullOrEmpty(description)) form["description"] = description;
        if (includeRam) form["vmstate"] = "1";

        return PostWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/snapshot", form, ct);
    }
    /// <summary>Rolls a guest back to a snapshot (POST .../snapshot/{name}/rollback). Returns the task UPID.</summary>
    [Versioning.PveApi("POST", "/nodes/{node}/qemu/{vmid}/snapshot/{snapname}/rollback")]
    [Versioning.PveApi("POST", "/nodes/{node}/lxc/{vmid}/snapshot/{snapname}/rollback")]
    public Task<string> RollbackSnapshotAsync(string node, ResourceKind kind, int vmid, string name,
        CancellationToken ct = default)
    {
        return PostWriteAsync(
            $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/snapshot/{Escape(name)}/rollback",
            null,
            ct);
    }
    /// <summary>Deletes a snapshot (DELETE .../snapshot/{name}). Returns the task UPID.</summary>
    [Versioning.PveApi("DELETE", "/nodes/{node}/qemu/{vmid}/snapshot/{snapname}")]
    [Versioning.PveApi("DELETE", "/nodes/{node}/lxc/{vmid}/snapshot/{snapname}")]
    public Task<string> DeleteSnapshotAsync(string node, ResourceKind kind, int vmid, string name,
        CancellationToken ct = default)
    {
        return DeleteWriteAsync($"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/snapshot/{Escape(name)}", ct);
    }
    /// <summary>스냅숏 설명 바꾸기(PUT .../snapshot/{name}/config) — 스냅숏 내용은 그대로, 메모만 바뀐다.</summary>
    [Versioning.PveApi("PUT", "/nodes/{node}/qemu/{vmid}/snapshot/{snapname}/config")]
    [Versioning.PveApi("PUT", "/nodes/{node}/lxc/{vmid}/snapshot/{snapname}/config")]
    public Task<string> UpdateSnapshotDescriptionAsync(string node, ResourceKind kind, int vmid, string name,
        string description, CancellationToken ct = default)
    {
        var path = $"nodes/{Escape(node)}/{kind.ApiSegment()}/{vmid}/snapshot/{Escape(name)}/config";
        return PutActionAsync(path, new Dictionary<string, string> { ["description"] = description }, ct);
    }
    private static PveSnapshot MapSnapshot(SnapshotDto dto)
    {
        return new PveSnapshot
        {
            Name = dto.Name ?? string.Empty,
            Description = string.IsNullOrEmpty(dto.Description) ? null : dto.Description,
            SnapTimeUtc = dto.SnapTime is > 0
                ? DateTimeOffset.FromUnixTimeSeconds(dto.SnapTime.Value).UtcDateTime
                : null,
            HasRam = (dto.Running ?? 0) != 0,
            Parent = string.IsNullOrEmpty(dto.Parent) ? null : dto.Parent
        };
    }
    private sealed class SnapshotDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("snaptime")] public long? SnapTime { get; set; }
        [JsonPropertyName("running")] public int? Running { get; set; }
        [JsonPropertyName("parent")] public string? Parent { get; set; }
    }
}
