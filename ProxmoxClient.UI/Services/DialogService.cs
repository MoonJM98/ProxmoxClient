using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Services;

/// <summary>
///     화면 위에 겹쳐 띄우는 대화상자·알림(토스트)·진행 표시. 모바일에는 별도 창이 없으므로
///     최상위 셸(<see cref="Views.AppShell" />)의 덮개 층에 그린다. 한 번에 대화상자 하나만 보인다.
/// </summary>
public sealed partial class DialogService : ObservableObject
{
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(3);
    private readonly Queue<DialogRequest> _pending = new();
    private DispatcherTimer? _toastTimer;
    /// <summary>떠 있는 진행 표시들(겹쳐 불릴 수 있다) — 맨 뒤가 화면에 보이는 것.</summary>
    private readonly List<BusyScope> _busyScopes = [];

    [ObservableProperty] public partial DialogRequest? Current { get; private set; }

    [ObservableProperty] public partial string? ToastText { get; private set; }

    /// <summary>직접 만든 내용의 대화상자(입력 폼 등). 한 번에 하나.</summary>
    [ObservableProperty] public partial Avalonia.Controls.Control? Sheet { get; private set; }

    public bool IsSheetOpen => Sheet is not null;

    private TaskCompletionSource? _sheetClosed;

    partial void OnSheetChanged(Avalonia.Controls.Control? value)
    {
        OnPropertyChanged(nameof(IsSheetOpen));
    }

    /// <summary>
    ///     내용을 화면 위에 띄우고 <see cref="CloseSheet" /> 가 불릴 때까지 기다린다(결과는 내용이 스스로 보관).
    ///     이미 열린 것이 있으면 그것이 닫힌 뒤에 띄운다.
    /// </summary>
    public async Task ShowSheetAsync(Avalonia.Controls.Control content)
    {
        while (_sheetClosed is { } open) await open.Task;

        _sheetClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = _sheetClosed.Task;
        Sheet = content;
        await closed;
    }

    public void CloseSheet()
    {
        var open = _sheetClosed;
        _sheetClosed = null;
        Sheet = null;
        open?.TrySetResult();
    }

    [ObservableProperty] public partial string? BusyText { get; private set; }

    public bool IsDialogOpen => Current is not null;

    public bool IsToastVisible => ToastText is not null;

    public bool IsBusyVisible => BusyText is not null;

    /// <summary>진행 표시에 취소 단추가 있다(오래 걸리는 연결 등).</summary>
    public bool CanCancelBusy => TopBusy?.Cancel is not null;

    public string BusyCancelText => L.T("Common_Cancel");

    private BusyScope? TopBusy => _busyScopes.Count > 0 ? _busyScopes[^1] : null;

    partial void OnCurrentChanged(DialogRequest? value)
    {
        OnPropertyChanged(nameof(IsDialogOpen));
    }

    partial void OnToastTextChanged(string? value)
    {
        OnPropertyChanged(nameof(IsToastVisible));
    }

    partial void OnBusyTextChanged(string? value)
    {
        OnPropertyChanged(nameof(IsBusyVisible));
    }

    public Task AlertAsync(string title, string message)
    {
        return ShowAsync(new DialogRequest(title, message) { HasCancel = false });
    }

    public async Task<bool> ConfirmAsync(string title, string message, string? confirmText = null,
        bool destructive = false)
    {
        return await ShowAsync(new DialogRequest(title, message)
        {
            ConfirmText = confirmText ?? L.T("Common_Ok"),
            IsDestructive = destructive
        });
    }

    /// <summary>한 줄 입력 — 취소하면 null. <paramref name="checkLabel" /> 을 주면 확인란 하나를 함께 보여 준다.</summary>
    public async Task<PromptResult?> PromptAsync(string title, string message, string? initial = null,
        bool isPassword = false, string? watermark = null, string? checkLabel = null, bool isChecked = false)
    {
        var request = new DialogRequest(title, message)
        {
            HasInput = true,
            InputText = initial ?? string.Empty,
            IsPassword = isPassword,
            InputWatermark = watermark,
            CheckLabel = checkLabel,
            IsChecked = isChecked
        };
        return await ShowAsync(request) ? new PromptResult(request.InputText, request.IsChecked) : null;
    }

    public void Toast(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ToastText = message;
            _toastTimer ??= CreateToastTimer();
            _toastTimer.Stop();
            _toastTimer.Start();
        });
    }

    /// <summary>
    ///     화면 전체를 막는 진행 표시 — 돌려준 값을 Dispose 하면 사라진다(겹쳐 불러도 된다).
    ///     <paramref name="cancel" /> 을 주면 취소 단추를 보이고, 누르거나 뒤로 가기를 하면 부른다(한 번만).
    /// </summary>
    public IDisposable ShowBusy(string text, Action? cancel = null)
    {
        var scope = new BusyScope(this, text, cancel);
        _busyScopes.Add(scope);
        UpdateBusy();
        return scope;
    }

    private void UpdateBusy()
    {
        BusyText = TopBusy?.Text;
        OnPropertyChanged(nameof(CanCancelBusy));
    }

    /// <summary>진행 중인 작업을 취소한다(취소할 수 있을 때만 true).</summary>
    [RelayCommand]
    private void CancelBusy()
    {
        TryCancelBusy();
    }

    public bool TryCancelBusy()
    {
        if (TopBusy is not { Cancel: { } cancel } top) return false;

        top.Cancel = null; // 두 번 누르지 않게 — 작업이 정리되며 진행 표시가 닫힌다
        OnPropertyChanged(nameof(CanCancelBusy));
        cancel();
        return true;
    }

    /// <summary>
    ///     뒤로 가기 — 열린 대화상자가 있으면 취소하고 true. 대화상자가 없고 진행 표시가 떠 있으면 취소할 수 있을 때
    ///     취소하고, 어느 쪽이든 진행 중에는 true(뒤 화면으로 넘어가지 않게).
    /// </summary>
    public bool HandleBack()
    {
        if (TryCancelCurrent()) return true;
        if (!IsBusyVisible) return false;

        TryCancelBusy();
        return true;
    }

    /// <summary>열린 대화상자가 있으면 취소하고 true.</summary>
    public bool TryCancelCurrent()
    {
        if (Current is null && Sheet is IDismissible sheet)
        {
            sheet.Dismiss();
            return true;
        }

        if (Current is not { } current) return false;

        current.CancelCommand.Execute(null);
        return true;
    }

    private Task<bool> ShowAsync(DialogRequest request)
    {
        request.Closed += OnClosed;
        if (Current is null)
            Current = request;
        else
            _pending.Enqueue(request);

        return request.Result;
    }

    private void OnClosed(DialogRequest request)
    {
        request.Closed -= OnClosed;
        if (!ReferenceEquals(Current, request)) return;

        Current = _pending.TryDequeue(out var next) ? next : null;
    }

    private DispatcherTimer CreateToastTimer()
    {
        var timer = new DispatcherTimer { Interval = ToastDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ToastText = null;
        };
        return timer;
    }

    /// <summary>진행 표시 한 겹.</summary>
    private sealed class BusyScope(DialogService owner, string text, Action? cancel) : IDisposable
    {
        public string Text { get; } = text;

        public Action? Cancel { get; set; } = cancel;

        public void Dispose()
        {
            if (!owner._busyScopes.Remove(this)) return;

            owner.UpdateBusy();
        }
    }
}

public sealed record PromptResult(string Text, bool IsChecked);

/// <summary>뒤로 가기로 닫을 수 있는 시트(취소와 같다).</summary>
public interface IDismissible
{
    void Dismiss();
}

/// <summary>대화상자 하나 — 확인/취소를 누르면 <see cref="Result" /> 가 끝난다.</summary>
public sealed partial class DialogRequest(string title, string message) : ObservableObject
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Title { get; } = title;

    public string Message { get; } = message;

    public bool HasMessage => Message.Length > 0;

    public string ConfirmText { get; init; } = L.T("Common_Ok");

    public string CancelText { get; init; } = L.T("Common_Cancel");

    public bool HasCancel { get; init; } = true;

    public bool IsDestructive { get; init; }

    public bool HasInput { get; init; }

    public bool IsPassword { get; init; }

    public char PasswordChar => IsPassword ? '•' : '\0';

    public string? InputWatermark { get; init; }

    public string? CheckLabel { get; init; }

    public bool HasCheck => CheckLabel is not null;

    [ObservableProperty] public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsChecked { get; set; }

    public Task<bool> Result => _result.Task;

    public event Action<DialogRequest>? Closed;

    [RelayCommand]
    private void Confirm()
    {
        Close(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Close(false);
    }

    private void Close(bool ok)
    {
        if (!_result.TrySetResult(ok)) return;

        Closed?.Invoke(this);
    }
}
