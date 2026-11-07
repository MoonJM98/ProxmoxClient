using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace ProxmoxClient.UI.Views.Storage;

/// <summary>
///     저장소 파일 올리기·받기 — 시스템 파일 선택기(StorageProvider)를 쓴다. 안드로이드 선택기는 로컬 경로가 없는
///     URI 를 줄 수 있으므로, 올리기는 선택기 스트림을 그대로 흘려 보낸다(<see cref="UploadSource" />).
/// </summary>
internal static class StorageFiles
{
    /// <summary>WPF 의 OpenFileDialog 필터 문자열("이름|*.a;*.b|...")을 선택기 형식으로 바꾼다.</summary>
    public static async Task<IStorageFile?> PickUploadAsync(object? owner, string filter, string title)
    {
        if (TopLevel.GetTopLevel(owner as Visual)?.StorageProvider is not { CanOpen: true } provider) return null;

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false, FileTypeFilter = ParseFilter(filter)
        });
        return files.Count == 0 ? null : files[0];
    }

    /// <summary>받을 위치를 고른다. 파일은 모든 형식, 폴더는 zip 으로 받는다(WPF 와 같은 제안 이름).</summary>
    public static async Task<IStorageFile?> PickSaveAsync(object? owner, string suggestedName, bool zip)
    {
        if (TopLevel.GetTopLevel(owner as Visual)?.StorageProvider is not { CanSave: true } provider) return null;

        return await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = zip ? "zip" : null,
            FileTypeChoices = zip
                ? [new FilePickerFileType("Zip (*.zip)") { Patterns = ["*.zip"] }]
                : [FilePickerFileTypes.All]
        });
    }

    /// <summary>테마의 아이콘 기하 도형(없으면 null).</summary>
    public static Geometry? Icon(string key)
    {
        return Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            ? value as Geometry
            : null;
    }

    /// <summary>파일 이름으로 쓸 수 없는 문자를 밑줄로 바꾼다.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return chars.Length == 0 ? "file" : new string(chars);
    }

    private static IReadOnlyList<FilePickerFileType> ParseFilter(string filter)
    {
        var parts = filter.Split('|');
        var types = new List<FilePickerFileType>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var patterns = parts[i + 1].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            types.Add(new FilePickerFileType(parts[i].Trim()) { Patterns = patterns });
        }

        return types;
    }
}

/// <summary>앱 전용 임시 폴더 — 쓰고 나면 통째로 지운다. 다른 파일에는 손대지 않는다.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Folder = Path.Combine(Path.GetTempPath(), "proxmox-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // 임시 파일이 아직 열려 있으면 운영체제가 치운다
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
///     올릴 파일의 로컬 경로. 경로가 있으면 그대로 쓰고, 없으면(안드로이드 콘텐츠 URI) 같은 이름으로 임시 폴더에 복사한다.
/// </summary>
internal sealed class LocalFileCopy : IAsyncDisposable
{
    private readonly TempFolder? _temp;

    private LocalFileCopy(string path, TempFolder? temp)
    {
        Path = path;
        _temp = temp;
    }

    /// <summary>업로드 본문의 파일 이름은 경로의 이름을 쓰므로 원래 이름을 그대로 둔다.</summary>
    public string Path { get; }

    public static async Task<LocalFileCopy> ForAsync(IStorageFile file)
    {
        if (file.TryGetLocalPath() is { } local) return new LocalFileCopy(local, null);

        var temp = new TempFolder();
        var copy = System.IO.Path.Combine(temp.Folder, StorageFiles.SafeFileName(file.Name));
        await using var source = await file.OpenReadAsync();
        await using var target = File.Create(copy);
        await source.CopyToAsync(target);
        return new LocalFileCopy(copy, temp);
    }

    public ValueTask DisposeAsync()
    {
        _temp?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
///     올릴 파일의 스트림과 길이 — 로컬 경로가 있으면 그 파일을, 없으면(Android content://) 선택기 스트림을 그대로 쓴다.
///     길이를 알 수 없는 공급자일 때만 임시 복사본을 만든다(서버는 길이 없는 청크 전송을 받지 못한다).
/// </summary>
internal sealed class UploadSource : IAsyncDisposable
{
    private Stream? _stream;
    private readonly LocalFileCopy? _copy;

    private UploadSource(Stream stream, long length, LocalFileCopy? copy)
    {
        _stream = stream;
        Length = length;
        _copy = copy;
    }

    public long Length { get; }

    public static async Task<UploadSource> OpenAsync(IStorageFile file)
    {
        if (file.TryGetLocalPath() is { } local)
        {
            var fs = File.OpenRead(local);
            return new UploadSource(fs, fs.Length, null);
        }

        var stream = await file.OpenReadAsync();
        if (stream.CanSeek) return new UploadSource(stream, stream.Length - stream.Position, null);

        ulong? size = null;
        try
        {
            size = (await file.GetBasicPropertiesAsync()).Size;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // 크기를 못 읽으면 아래에서 복사본으로
        }

        if (size is > 0) return new UploadSource(stream, (long)size.Value, null);

        await stream.DisposeAsync();
        var copy = await LocalFileCopy.ForAsync(file);
        var copied = File.OpenRead(copy.Path);
        return new UploadSource(copied, copied.Length, copy);
    }

    /// <summary>스트림을 넘긴다 — 받은 쪽(업로드)이 닫는다.</summary>
    public Stream TakeStream()
    {
        var stream = _stream ?? throw new InvalidOperationException("already taken");
        _stream = null;
        return stream;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null) await _stream.DisposeAsync();
        if (_copy is not null) await _copy.DisposeAsync();
    }
}
