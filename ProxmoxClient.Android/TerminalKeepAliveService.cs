using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;

namespace ProxmoxClient.Droid;

/// <summary>
///     터미널 연결 유지 — 터미널(CT 콘솔·노드 셸)이 연결된 동안 포그라운드 서비스로 떠서 알림을 보인다.
///     Android 는 화면에 없는 앱을 곧 멈추거나 네트워크를 막는데, 포그라운드 서비스가 있으면 앱을 살려 둔다
///     (SSH 앱들과 같은 방식). 알림을 누르면 앱으로 돌아오고, [모두 끊기]로 연결을 모두 끊는다.
///     manifest 에 specialUse 형식으로 선언했다(Android 14 이상은 형식이 필요하다).
/// </summary>
[Register("io.github.moonjm98.proxmoxclient.TerminalKeepAliveService")]
internal sealed class TerminalKeepAliveService : Service
{
    private const string ChannelId = "terminal_keepalive";
    private const int NotificationId = 1001;
    private const string ActionDisconnect = "io.github.moonjm98.proxmoxclient.action.DISCONNECT_TERMINALS";
    private const string ExtraText = "text";

    /// <summary>
    ///     서비스가 포그라운드로 떠 있다(StartForeground 를 마쳤다) — 알림만 바꿀 때 서비스를 다시 시작하지 않는다
    ///     (배경에서는 시작이 막힌다). 이때만 StopService 로 멈춰도 된다.
    /// </summary>
    public static bool IsRunning { get; private set; }

    /// <summary>
    ///     StartForegroundService 를 불렀지만 아직 OnStartCommand 가 오지 않았다. 이 사이에 StopService 하면
    ///     "startForeground 를 부르지 않았다"로 앱이 죽는다 — 멈춤은 <see cref="DesiredText" /> 를 비워 두는 것으로 알리고,
    ///     서비스가 시작하자마자 StartForeground 를 부른 뒤 스스로 멈춘다.
    /// </summary>
    public static bool StartPending { get; set; }

    /// <summary>지금 보여야 할 알림 문구 — null 이면 세션이 없다(서비스는 멈춰야 한다). UI(메인) 스레드에서만 바꾼다.</summary>
    public static string? DesiredText { get; set; }

    public override IBinder? OnBind(Intent? intent)
    {
        return null;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionDisconnect)
        {
            SessionKeepAlive.RequestDisconnectAll(); // 터미널이 모두 끊기면 Host 가 서비스를 멈춘다
            return StartCommandResult.NotSticky;
        }

        // StartForegroundService 로 시작했으면 무슨 일이 있어도 먼저 StartForeground 를 불러야 한다
        StartPending = false;
        var text = DesiredText;
        var notification = Build(this, text ?? intent?.GetStringExtra(ExtraText) ?? string.Empty);
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);

        if (text is null)
        {
            // 시작을 기다리는 사이 터미널이 모두 끊겼다 — 바로 내려간다
            IsRunning = false;
            StopForegroundCompat();
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        IsRunning = true;
        return StartCommandResult.NotSticky; // 앱이 죽으면 세션도 없다 — 다시 띄우지 않는다
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        StartPending = false;
        base.OnDestroy();
    }

    private void StopForegroundCompat()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(24))
            StopForeground(StopForegroundFlags.Remove);
        else
#pragma warning disable CA1422
            StopForeground(true);
#pragma warning restore CA1422
    }

    /// <summary>알림 문구 바꾸기(서비스가 떠 있을 때).</summary>
    public static void UpdateText(Context context, string text)
    {
        NotificationManagerCompat.From(context)?.Notify(NotificationId, Build(context, text));
    }

    public static Intent StartIntent(Context context, string text)
    {
        var intent = new Intent(context, typeof(TerminalKeepAliveService));
        intent.PutExtra(ExtraText, text);
        return intent;
    }

    private static Notification Build(Context context, string text)
    {
        EnsureChannel(context);
        var open = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        open?.SetFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
        var openPending = open is null
            ? null
            : PendingIntent.GetActivity(context, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var disconnect = new Intent(context, typeof(TerminalKeepAliveService)).SetAction(ActionDisconnect);
        var disconnectPending = PendingIntent.GetService(context, 1, disconnect,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        var builder = new NotificationCompat.Builder(context, ChannelId);
        builder.SetSmallIcon(Resource.Drawable.ic_stat_terminal);
        builder.SetContentTitle(L.T("KeepAlive_Title"));
        builder.SetContentText(text);
        builder.SetOngoing(true);
        builder.SetSilent(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetCategory(NotificationCompat.CategoryService);
        builder.SetPriority(NotificationCompat.PriorityLow);
        builder.SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate);
        builder.SetContentIntent(openPending);
        builder.AddAction(0, L.T("KeepAlive_DisconnectAll"), disconnectPending);
        return builder.Build()!;
    }

    private static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

        var channel = new NotificationChannel(ChannelId, L.T("KeepAlive_Channel"), NotificationImportance.Low)
        {
            Description = L.T("KeepAlive_ChannelDesc")
        };
        channel.SetShowBadge(false);
        (context.GetSystemService(Context.NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
    }
}

/// <summary>
///     <see cref="SessionKeepAlive" /> 의 Android 구현 — 세션이 생기면 서비스를 띄우고, 바뀌면 알림 문구만 바꾸고, 비면 멈춘다.
///     처음 띄울 때 Android 13 이상이면 알림 권한을 묻는다(거부해도 서비스는 돌고 연결도 유지된다 — 알림만 안 보인다).
/// </summary>
internal sealed class AndroidSessionKeepAliveHost(Context context) : ISessionKeepAliveHost
{
    private bool _askedPermission;
    private long _startRequestedAt;

    public void Update(IReadOnlyList<string> titles)
    {
        if (titles.Count == 0)
        {
            TerminalKeepAliveService.DesiredText = null;
            // 시작 대기 중이면 멈추지 않는다 — 서비스가 시작하면 StartForeground 뒤 스스로 멈춘다(바로 멈추면 앱이 죽는다)
            if (TerminalKeepAliveService.IsRunning)
                context.StopService(new Intent(context, typeof(TerminalKeepAliveService)));
            return;
        }

        var text = titles.Count == 1 ? titles[0] : L.T("KeepAlive_Many", titles.Count, string.Join(", ", titles));
        TerminalKeepAliveService.DesiredText = text;
        if (TerminalKeepAliveService.IsRunning)
        {
            TerminalKeepAliveService.UpdateText(context, text);
            return;
        }

        // 시작하면 DesiredText 로 알림을 띄운다. 시작이 오래(10초) 오지 않으면 다시 시도한다
        if (TerminalKeepAliveService.StartPending &&
            global::Android.OS.SystemClock.UptimeMillis() - _startRequestedAt < 10_000) return;

        AskNotificationPermission();
        try
        {
            TerminalKeepAliveService.StartPending = true;
            _startRequestedAt = global::Android.OS.SystemClock.UptimeMillis();
            ContextCompat.StartForegroundService(context, TerminalKeepAliveService.StartIntent(context, text));
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.Lang.SecurityException)
        {
            // 배경에서 막혔다(Android 12 이상) — 이번에는 붙잡지 못한다. 연결은 그대로 두고 다음 연결 때 다시 시도
            TerminalKeepAliveService.StartPending = false;
            Android.Util.Log.Warn("ProxmoxClient", $"keep-alive service not started: {ex.Message}");
            AppLog.Warn($"keep-alive service not started: {ex.Message}");
        }
    }

    private void AskNotificationPermission()
    {
        if (_askedPermission || !OperatingSystem.IsAndroidVersionAtLeast(33)) return;

        _askedPermission = true;
        const string permission = "android.permission.POST_NOTIFICATIONS";
        if (ContextCompat.CheckSelfPermission(context, permission) == Permission.Granted) return;

        if (MainActivity.Current is { } activity) ActivityCompat.RequestPermissions(activity, [permission], 1001);
    }
}
