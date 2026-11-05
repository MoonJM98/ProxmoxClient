using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ProxmoxClient.Core.Files;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Storage;

namespace ProxmoxClient.UI.Views.Files;

/// <summary>파일 패널 한 줄 — WPF GuestFileRow 와 같은 값(정렬 키·표시 문구·아이콘).</summary>
public sealed class GuestFileRow(GuestFileEntry entry)
{
    public GuestFileEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string NameTip => Entry.LinkTarget is { } target ? $"{Entry.Name} → {target}" : Entry.Name;
    public string SortName => (Entry.OpensAsFolder ? "0" : "1") + Entry.Name;
    public long SortSize => Entry.OpensAsFolder ? -1 : Entry.Size;
    public DateTime SortModified => Entry.Modified ?? DateTime.MinValue;
    public string SizeText => Entry.Kind == GuestFileKind.File ? ByteFormatter.Format(Entry.Size) : string.Empty;

    /// <summary>숨김·시스템 항목은 흐리게.</summary>
    public double RowOpacity => Entry.IsHidden ? 0.5 : 1;

    public string ModifiedText => Entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    public string OwnerText => Entry.Owner.Length == 0 ? string.Empty
        : Entry.Group.Length == 0 || Entry.Group == Entry.Owner ? Entry.Owner
        : $"{Entry.Owner}:{Entry.Group}";

    public Geometry? Icon => StorageFiles.Icon(Entry.OpensAsFolder ? "IconFolder"
        : Entry.Kind == GuestFileKind.Link ? "IconLink"
        : "IconFile");

    public IBrush? IconBrush => Brush(Entry.OpensAsFolder ? "BrushAccent" : "BrushDim");

    /// <summary>폴더 링크는 폴더 아이콘 위에 작은 링크 표시를 겹친다.</summary>
    public bool LinkBadge => Entry.LinkToDirectory;

    private static IBrush? Brush(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            ? value as IBrush
            : null;
}
