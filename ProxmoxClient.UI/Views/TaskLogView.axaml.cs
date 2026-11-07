using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using ProxmoxClient.UI.ViewModels;

namespace ProxmoxClient.UI.Views;

/// <summary>모바일 페이지와 데스크톱 상세 창이 함께 쓰는 화면.</summary>
public partial class TaskLogView : UserControl
{
    /// <summary>맨 아래에서 이만큼 안이면 바닥으로 본다.</summary>
    private const double BottomSlack = 24;

    private TaskLogViewModel? _model;

    /// <summary>맨 아래를 보고 있다 — 새 줄이 오면 따라 내려간다(위로 올려 읽는 중이면 그대로 둔다).</summary>
    private bool _followTail = true;

    public TaskLogView()
    {
        InitializeComponent();
        LogScroll.ScrollChanged += OnScrollChanged;
        DataContextChanged += (_, _) => Attach(DataContext as TaskLogViewModel);
        AttachedToVisualTree += (_, _) => Attach(DataContext as TaskLogViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
    }

    /// <summary>지금 맨 아래를 따라가는지(검사용).</summary>
    public bool IsFollowingTail => _followTail;

    private void Attach(TaskLogViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;

        if (_model is not null) _model.Lines.CollectionChanged -= OnLinesChanged;
        _model = model;
        if (_model is not null) _model.Lines.CollectionChanged += OnLinesChanged;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 줄이 늘어 범위만 커진 경우는 사용자가 움직인 것이 아니다 — 위치·창 크기가 바뀔 때만 다시 판단
        if (e.OffsetDelta.Y == 0 && e.ViewportDelta.Y == 0) return;

        _followTail = LogScroll.Offset.Y + LogScroll.Viewport.Height >= LogScroll.Extent.Height - BottomSlack;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_followTail || e.Action != NotifyCollectionChangedAction.Add) return;

        // 배치가 끝난 뒤 — 가상화 목록은 줄 높이를 어림하므로 한 번 더 맞춘다
        Dispatcher.UIThread.Post(() =>
        {
            LogScroll.ScrollToEnd();
            Dispatcher.UIThread.Post(LogScroll.ScrollToEnd, DispatcherPriority.Background);
        }, DispatcherPriority.Loaded);
    }
}
