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
    private static readonly Dictionary<object, string> Sessions = [];
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
        if (!Sessions.ContainsKey(owner)) Order.Add(owner);
        Sessions[owner] = title;
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
        var titles = Order.Select(owner => Sessions[owner]).ToList();
        Host?.Update(titles);
        Persist(titles);
    }

    /// <summary>
    ///     열린 터미널 제목을 남겨 두는 파일 — 휴대폰·태블릿에서 앱이 터미널을 연 채로 죽으면(최근 앱에서 밀어 닫기,
    ///     메모리 부족 등) 다음 실행 때 무엇이 끊겼는지 알린다. 정상적으로 닫으면 지워진다.
    /// </summary>
    public static string StatePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProxmoxClient", "open-terminals.txt");

    /// <summary>
    ///     지난 실행에서 열려 있던 터미널 제목(앱이 그대로 죽었을 때만 남는다). 읽고 나면 지운다.
    ///     데스크톱은 창을 닫아 끝내는 것이 정상 종료라 남기지 않는다.
    /// </summary>
    public static IReadOnlyList<string> TakeOrphanedSessions()
    {
        try
        {
            if (!File.Exists(StatePath)) return [];

            var titles = File.ReadAllLines(StatePath).Where(line => line.Length > 0).ToList();
            File.Delete(StatePath);
            return titles;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Persist(IReadOnlyList<string> titles)
    {
        if (!PlatformInfo.IsTouch) return;

        try
        {
            if (titles.Count == 0)
            {
                if (File.Exists(StatePath)) File.Delete(StatePath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllLines(StatePath, titles.Select(title => title.ReplaceLineEndings(" ")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"open terminal list not saved: {ex.Message}");
        }
    }
}
