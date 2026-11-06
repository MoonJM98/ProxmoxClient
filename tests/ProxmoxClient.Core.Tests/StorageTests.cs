using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProxmoxClient.Core.Files;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Settings;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.Core.Tests;

/// <summary>임시 폴더 — 테스트가 끝나면 지운다.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pvc-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir()
    {
        Directory.CreateDirectory(Path);
    }

    public string File(string name)
    {
        return System.IO.Path.Combine(Path, name);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}

public class AtomicFileTests
{
    [Fact]
    public async Task WritesNewFileAndKeepsBackupOnReplace()
    {
        using var dir = new TempDir();
        var path = dir.File("sub/a.json"); // 없는 폴더도 만든다

        await AtomicFile.WriteAllTextAsync(path, "첫째");
        Assert.Equal("첫째", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".bak"));

        await AtomicFile.WriteAllTextAsync(path, "둘째");
        Assert.Equal("둘째", await File.ReadAllTextAsync(path));
        Assert.Equal("첫째", await File.ReadAllTextAsync(path + ".bak"));

        // 임시 파일이 남지 않는다
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task WritesUtf8WithoutBom()
    {
        using var dir = new TempDir();
        var path = dir.File("b.json");
        await AtomicFile.WriteAllTextAsync(path, "{}");
        Assert.Equal("{}"u8.ToArray(), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ConcurrentWritesToSamePathAllSucceed()
    {
        using var dir = new TempDir();
        var path = dir.File("c.json");
        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => AtomicFile.WriteAllTextAsync(path, i.ToString())));
        Assert.InRange(int.Parse(await File.ReadAllTextAsync(path)), 0, 19);
    }

    [Fact]
    public void PreserveCorruptCopiesOnce()
    {
        using var dir = new TempDir();
        var path = dir.File("d.json");
        File.WriteAllText(path, "{broken");

        var copy = AtomicFile.PreserveCorrupt(path);
        Assert.NotNull(copy);
        Assert.Equal("{broken", File.ReadAllText(copy));
        Assert.Equal("{broken", File.ReadAllText(path)); // 원본은 그대로
        Assert.Equal(copy, AtomicFile.PreserveCorrupt(path)); // 같은 내용이면 새 사본을 만들지 않는다
        Assert.Null(AtomicFile.PreserveCorrupt(dir.File("missing.json")));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public async Task AppSettingsRoundTrip()
    {
        using var dir = new TempDir();
        var store = new AppSettingsStore(dir.File("app-settings.json"));
        var settings = new AppSettings
        {
            RefreshIntervalSeconds = 7, AutoRefreshOnStart = false, ByteUnit = ByteDisplayUnit.GB,
            ShowTemplates = false
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(settings.Normalize(), loaded);
        // 열거형은 이름(문자열)으로 저장한다 — 사람이 읽고 고칠 수 있게
        Assert.Contains("\"GB\"", await File.ReadAllTextAsync(dir.File("app-settings.json")));
    }

    [Fact]
    public async Task ConsoleSettingsRoundTripWithDictionaries()
    {
        using var dir = new TempDir();
        var store = new ConsoleSettingsStore(dir.File("console-settings.json"));
        var id = Guid.NewGuid();
        var settings = new ConsoleSettings
        {
            Encoding = ConsoleSettings.VncEncoding.Zlib,
            TerminalFontSize = 15,
            SftpHostKeys = new Dictionary<string, string> { ["[10.0.0.1]:22"] = "SHA256:abc" },
            SftpConnections = new Dictionary<Guid, SftpConnectionProfile>
                { [id] = new() { Id = id, Name = "box", Host = "10.0.0.1" } }
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(ConsoleSettings.VncEncoding.Zlib, loaded.Encoding);
        Assert.Equal(15, loaded.TerminalFontSize);
        Assert.Equal("SHA256:abc", loaded.SftpHostKeys["[10.0.0.1]:22"]);
        Assert.Equal("box", loaded.SftpConnections[id].Name);
    }

    [Fact]
    public async Task MissingFileGivesDefaults()
    {
        using var dir = new TempDir();
        var loaded = await new AppSettingsStore(dir.File("none.json")).LoadAsync();
        Assert.Equal(new AppSettings().Normalize(), loaded);
    }

    [Fact]
    public async Task CorruptFileGivesDefaultsAndKeepsCopy()
    {
        using var dir = new TempDir();
        var path = dir.File("app-settings.json");
        await File.WriteAllTextAsync(path, "{ not json");

        var loaded = await new AppSettingsStore(path).LoadAsync();

        Assert.Equal(new AppSettings().Normalize(), loaded);
        Assert.Single(Directory.EnumerateFiles(dir.Path, "app-settings.json.corrupt-*"));
    }

    [Fact]
    public async Task ReadsEnumAsNumberAndIgnoresCase()
    {
        using var dir = new TempDir();
        var path = dir.File("app-settings.json");
        await File.WriteAllTextAsync(path, """{"byteunit": 2, "SHOWTEMPLATES": false}""");

        var loaded = await new AppSettingsStore(path).LoadAsync();

        Assert.Equal(ByteDisplayUnit.MB, loaded.ByteUnit);
        Assert.False(loaded.ShowTemplates);
    }

    [Fact]
    public async Task LastLoginRoundTrip()
    {
        using var dir = new TempDir();
        var store = new LastLoginStore(dir.File("last-login.json"));
        Assert.Null(await store.LoadAsync());

        var value = new LastLogin(Guid.NewGuid(), "pam");
        await store.SaveAsync(value);
        Assert.Equal(value, await store.LoadAsync());
    }

    [Fact]
    public async Task ProfileRoundTrip()
    {
        using var dir = new TempDir();
        var store = new ProfileStore(dir.Path);
        var profile = new ConnectionProfile
        {
            Name = "lab", Host = "pve.example", Port = 8007, UserName = "admin@pve",
            AuthMode = AuthMode.ApiToken, ApiTokenId = "admin@pve!ci"
        };

        await store.SaveAsync(profile);
        var loaded = Assert.Single(await store.LoadAsync());

        Assert.Equal(profile.Id, loaded.Id);
        Assert.Equal("lab", loaded.Name);
        Assert.Equal("pve.example", loaded.Host);
        Assert.Equal(8007, loaded.Port);
        Assert.Equal("admin@pve", loaded.UserName);
        Assert.Equal(AuthMode.ApiToken, loaded.AuthMode);
        Assert.Equal("admin@pve!ci", loaded.ApiTokenId);
    }
}

/// <summary>
///     빌드 때 만든 JSON 직렬화(CoreJsonContext.cs)가 예전 리플렉션 직렬화와 같은 파일을 쓰는지 —
///     저장 파일 모양이 바뀌면 이전 버전과 설정을 주고받지 못한다.
/// </summary>
public class JsonSourceGenerationTests
{
    private static readonly JsonSerializerOptions SettingsOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions ProfileOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void SettingsMatchReflection()
    {
        var app = new AppSettings { ByteUnit = ByteDisplayUnit.TB, Language = "en" };
        Assert.Equal(JsonSerializer.Serialize(app, SettingsOptions),
            JsonSerializer.Serialize(app, SettingsJsonContext.Default.AppSettings));

        var id = Guid.NewGuid();
        var console = new ConsoleSettings
        {
            LocalCursor = ConsoleSettings.LocalCursorMode.Dot,
            FtpCertificates = new Dictionary<string, string> { ["h"] = "x" },
            SftpAccounts = new Dictionary<Guid, SftpAccountProfile> { [id] = new() { Id = id, Name = "a" } }
        };
        Assert.Equal(JsonSerializer.Serialize(console, SettingsOptions),
            JsonSerializer.Serialize(console, SettingsJsonContext.Default.ConsoleSettings));
    }

    [Fact]
    public void ProfileMatchesReflection()
    {
        var profile = new ConnectionProfile
        {
            Name = "p", Host = "h", ProxyMode = ProxyMode.Socks5, ProxyHost = "proxy", ProxyPort = 1080,
            CertificateThumbprint = "AB"
        };
        Assert.Equal(JsonSerializer.Serialize(profile, ProfileOptions),
            JsonSerializer.Serialize(profile, ProfileJsonContext.Default.ConnectionProfile));
    }

    [Fact]
    public void EverySettingsStoreTypeIsGenerated()
    {
        // JsonSettingsStore<T> 를 상속한 저장소의 T 는 모두 SettingsJsonContext 에 있어야 한다(없으면 리플렉션으로 떨어짐)
        var settingTypes = typeof(JsonSettingsStore<>).Assembly.GetTypes()
            .Select(t => t.BaseType)
            .Where(b => b is { IsGenericType: true } && b.GetGenericTypeDefinition() == typeof(JsonSettingsStore<>))
            .Select(b => b!.GetGenericArguments()[0])
            .ToList();
        Assert.NotEmpty(settingTypes);
        foreach (var type in settingTypes)
            Assert.True(SettingsJsonContext.Default.GetTypeInfo(type) is not null, $"{type.Name} 이 SettingsJsonContext 에 없다");
    }
}
