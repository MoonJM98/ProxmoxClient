using System.Text.Json;
using System.Text.Json.Serialization;
using ProxmoxClient.Core.Models;
using ProxmoxClient.Core.Profiles;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     배경에서도 터미널 연결을 살려 두는 장치 — 운영체제가 앱을 멈추지 않게 붙잡는다.
///     Android: 포그라운드 서비스(알림 "터미널 연결 유지 중" + [모두 끊기]). 헤드 프로젝트가 시작할 때
///     <see cref="SessionKeepAlive.Host" /> 에 넣는다.
/// </summary>
public interface ISessionKeepAliveHost
{
    /// <summary>살려 둘 세션 제목들이 바뀌었다 — 비면 붙잡기를 멈춘다. UI 스레드에서 불린다.</summary>
    void Update(IReadOnlyList<string> titles);
}

/// <summary>
///     연결된 터미널 세션 목록. 터미널 화면은 연결되면 <see cref="Acquire" />, 끊기거나 닫히면 <see cref="Release" /> 한다.
///     <see cref="Host" /> 가 없으면(데스크톱·iOS) 아무 일도 하지 않고, 터미널은 예전처럼 배경에서 끊었다가 돌아오면 다시 연결한다.
/// </summary>
public static class SessionKeepAlive
{
    private static readonly Dictionary<object, OpenTerminalEntry> Sessions = [];
    private static readonly List<object> Order = [];

    /// <summary>바꿔 넣으면 지금 연결된 세션을 바로 알려 준다.</summary>
    public static ISessionKeepAliveHost? Host
    {
        get => _host;
        set
        {
            _host = value;
            if (Sessions.Count > 0) Notify();
        }
    }

    private static ISessionKeepAliveHost? _host;

    /// <summary>배경에서도 연결을 살려 둘 수 있다 — 그러면 터미널은 배경으로 가도 끊지 않는다.</summary>
    public static bool IsAvailable => Host is not null;

    /// <summary>알림의 [모두 끊기] — 연결된 터미널이 모두 끊는다(UI 스레드에서 불린다).</summary>
    public static event Action? DisconnectAllRequested;

    public static void Acquire(object owner, string title)
    {
        Acquire(owner, new OpenTerminalEntry { Title = title });
    }

    /// <summary>다시 열 수 있게 대상까지 남긴다(앱이 죽은 뒤 다음 실행에서 다시 연다).</summary>
    public static void Acquire(object owner, OpenTerminalEntry entry)
    {
        if (!Sessions.ContainsKey(owner)) Order.Add(owner);
        Sessions[owner] = entry;
        Notify();
    }

    public static void Release(object owner)
    {
        if (!Sessions.Remove(owner)) return;

        Order.Remove(owner);
        Notify();
    }

    /// <summary>헤드 프로젝트(알림 단추)가 부른다 — 어느 스레드에서 불러도 된다.</summary>
    public static void RequestDisconnectAll()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => DisconnectAllRequested?.Invoke());
    }

    private static void Notify()
    {
        var entries = Order.Select(owner => Sessions[owner]).ToList();
        Host?.Update(entries.Select(entry => entry.Title).ToList());
        Persist();
    }

    /// <summary>
    ///     열린 터미널 목록을 남겨 두는 파일(내용은 JSON — 이전 버전의 제목 줄 파일은 버린다) — 휴대폰·태블릿에서 앱이 터미널을 연 채로 죽으면(최근 앱에서 밀어 닫기,
    ///     메모리 부족 등) 다음 실행 때 다시 열 수 있게 한다. 정상적으로 닫으면 지워진다.
    ///     데스크톱은 창을 닫아 끝내는 것이 정상 종료라 남기지 않는다.
    /// </summary>
    public static string StatePath
    {
        get => _statePath;
        set
        {
            _statePath = value;
            _pending = null; // 새 파일에서 다시 읽는다
        }
    }

    private static string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxmoxClient", "open-terminals.txt");

    private static List<OpenTerminalEntry>? _pending;

    /// <summary>
    ///     지난 실행에서 열려 있던(앱이 그대로 죽어 남은) 터미널 — 사용자가 다시 열거나 닫을 때까지 파일에도 남긴다
    ///     (다시 열기 전에 앱이 또 죽어도 잃지 않게). 다시 열 수 없는 기록(대상이 없는 것)은 버린다.
    /// </summary>
    public static IReadOnlyList<OpenTerminalEntry> Pending => PendingList;

    private static List<OpenTerminalEntry> PendingList
    {
        get
        {
            if (_pending is not null) return _pending;

            _pending = PlatformInfo.IsTouch ? ReadState(StatePath).Where(entry => entry.CanReopen).ToList() : [];
            return _pending;
        }
    }

    /// <summary>이 서버(프로필)에서 다시 열 터미널을 꺼낸다 — 목록과 파일에서 빠진다.</summary>
    public static IReadOnlyList<OpenTerminalEntry> TakePending(Guid profileId)
    {
        var taken = PendingList.Where(entry => entry.ProfileId == profileId).ToList();
        if (taken.Count == 0) return taken;

        PendingList.RemoveAll(entry => entry.ProfileId == profileId);
        Persist();
        return taken;
    }

    /// <summary>남은 프로필에 속하지 않은 기록(지운 서버)을 버린다.</summary>
    public static void DropPendingExcept(IEnumerable<Guid> profileIds)
    {
        var keep = profileIds.ToHashSet();
        if (PendingList.RemoveAll(entry => !keep.Contains(entry.ProfileId)) > 0) Persist();
    }

    /// <summary>파일 내용 — 못 읽거나 모양이 다르면(이전 버전 파일 포함) 빈 목록.</summary>
    public static List<OpenTerminalEntry> ReadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];

            return JsonSerializer.Deserialize(File.ReadAllText(path), UiJsonContext.Default.ListOpenTerminalEntry) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Persist()
    {
        if (!PlatformInfo.IsTouch) return;

        try
        {
            var entries = Order.Select(owner => Sessions[owner]).Concat(PendingList).ToList();
            if (entries.Count == 0)
            {
                if (File.Exists(StatePath)) File.Delete(StatePath);
                return;
            }

            // 다 쓴 뒤 바꿔 넣는다 — 쓰는 도중에 앱이 죽어도 반쪽 파일이 남지 않게
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, UiJsonContext.Default.ListOpenTerminalEntry));
            File.Move(temp, StatePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"open terminal list not saved: {ex.Message}");
        }
    }
}

/// <summary>열린 터미널 하나 — 다시 열 서버(프로필)와 대상. 파일(open-terminals.txt, 내용은 JSON)에 이 모양으로 남는다.</summary>
public sealed record OpenTerminalEntry
{
    /// <summary>서버 프로필(<see cref="ConnectionProfile.Id" />). 비어 있으면 다시 열 수 없다.</summary>
    public Guid ProfileId { get; init; }

    /// <summary>서버 이름(목록의 안내 문구).</summary>
    public string ProfileName { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Node { get; init; } = string.Empty;

    /// <summary>게스트 종류("Lxc"·"Qemu") — 노드 셸이면 null.</summary>
    public string? GuestKind { get; init; }

    public int VmId { get; init; }

    public string? GuestName { get; init; }

    /// <summary>노드 셸에서 실행한 명령(upgrade 등).</summary>
    public string? Command { get; init; }

    [JsonIgnore]
    public bool CanReopen => ProfileId != Guid.Empty && Node.Length > 0 && Title.Length > 0
                             && (GuestKind is null || (ParseKind() is not null && VmId > 0));

    public static OpenTerminalEntry From(ConnectionProfile profile, ConsoleTarget target, string title,
        PveResource? guest)
    {
        return new OpenTerminalEntry
        {
            ProfileId = profile.Id,
            ProfileName = string.IsNullOrWhiteSpace(profile.Name) ? profile.Host : profile.Name,
            Title = title,
            Node = target.Node,
            GuestKind = target.Kind?.ToString(),
            VmId = target.VmId,
            GuestName = guest?.Name,
            Command = target.Command
        };
    }

    public ConsoleTarget ToTarget()
    {
        return ParseKind() is { } kind ? ConsoleTarget.ForGuest(Node, kind, VmId) : ConsoleTarget.ForNode(Node, Command);
    }

    /// <summary>게스트 터미널이면 게스트(이름·노드만 채운 것 — 화면이 쓰는 정보).</summary>
    public PveResource? ToGuest()
    {
        if (ParseKind() is not { } kind) return null;

        return new PveResource
        {
            Id = $"{kind.ApiSegment()}/{VmId}",
            Kind = kind,
            VmId = VmId,
            Name = GuestName ?? string.Empty,
            Node = Node
        };
    }

    private ResourceKind? ParseKind()
    {
        return GuestKind is not null && Enum.TryParse<ResourceKind>(GuestKind, out var kind) ? kind : null;
    }
}
