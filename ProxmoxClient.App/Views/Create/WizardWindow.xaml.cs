using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Create;

/// <summary>마법사 한 단계 — 제목, 화면 그리기, 다음으로 넘어가기 전 검사, 처음 열 때 할 일.</summary>
internal sealed class WizardStep
{
    public required string TitleKey { get; init; }
    public required Action<WizardPage> Build { get; init; }

    /// <summary>문제가 있으면 보여 줄 문구(null 이면 통과).</summary>
    public Func<string?>? Validate { get; init; }

    /// <summary>단계를 열 때마다 — 웹 UI 처럼 처음 열 때의 OS 로 기본값을 정하는 단계가 쓴다.</summary>
    public Action? OnShown { get; init; }
}

/// <summary>
///     웹 UI 와 같은 단계별 만들기 창 — 왼쪽 단계 목록, 오른쪽 입력, 아래 이전·다음·만들기.
///     마지막 단계에서 <c>finish</c> 를 실행하고, 성공하면 창을 닫는다(실패 문구는 상태 줄에 남긴다).
/// </summary>
internal partial class WizardWindow : Window
{
    private readonly Func<Task<string>> _finish;
    private readonly IReadOnlyList<WizardStep> _steps;
    private bool _busy;
    private int _current = -1;
    private int _reached;

    public WizardWindow(string title, IReadOnlyList<WizardStep> steps, Func<Task<string>> finish)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Title = title;
        _steps = steps;
        _finish = finish;
        StepList.ItemsSource = steps.Select(s => Loc.T(s.TitleKey)).ToList();
        ShowStep(0);
    }

    /// <summary>만들기 결과 문구(성공했을 때).</summary>
    public string? Result { get; private set; }

    /// <summary>지금 단계를 다시 그린다(다른 칸에 영향을 주는 값이 바뀌었을 때).</summary>
    public void Rebuild()
    {
        var scroll = (PagePanel.Parent as ScrollViewer)?.VerticalOffset ?? 0;
        Render();
        (PagePanel.Parent as ScrollViewer)?.ScrollToVerticalOffset(scroll);
    }

    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    /// <summary>
    ///     노드 목록을 다시 읽거나 만드는 동안 — 입력·단계 이동·닫기를 막는다(끝나기 전 값이 섞이지 않게).
    /// </summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        PagePanel.IsEnabled = StepList.IsEnabled = AdvancedCheck.IsEnabled = !busy;
        BtnBack.IsEnabled = !busy && _current > 0;
        BtnNext.IsEnabled = BtnFinish.IsEnabled = BtnCancel.IsEnabled = !busy;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy) e.Cancel = true;
    }

    private void ShowStep(int index)
    {
        _current = index;
        _reached = Math.Max(_reached, index);
        _steps[index].OnShown?.Invoke();
        StepList.SelectedIndex = index;
        StatusText.Text = string.Empty;
        Render();
        var last = index == _steps.Count - 1;
        BtnBack.IsEnabled = index > 0;
        BtnNext.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        BtnFinish.Visibility = last ? Visibility.Visible : Visibility.Collapsed;
        BtnFinish.IsDefault = last;
        BtnNext.IsDefault = !last;
    }

    private void Render()
    {
        PagePanel.Children.Clear();
        _steps[_current].Build(new WizardPage(PagePanel, AdvancedCheck.IsChecked == true, this));
    }

    private void OnAdvancedChanged(object sender, RoutedEventArgs e)
    {
        if (_current >= 0) Rebuild();
    }

    private void OnStepClicked(object sender, SelectionChangedEventArgs e)
    {
        var target = StepList.SelectedIndex;
        if (target < 0 || target == _current) return;
        // 아직 거치지 않은 단계로는 건너뛸 수 없다(앞 단계 검사를 거쳐야 한다)
        if (target > _reached || target > _current && !CanLeave())
        {
            StepList.SelectedIndex = _current;
            return;
        }

        ShowStep(target);
    }

    private bool CanLeave()
    {
        if (_steps[_current].Validate?.Invoke() is not { } problem) return true;
        StatusText.Text = problem;
        return false;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (!_busy && _current > 0) ShowStep(_current - 1);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (!_busy && _current < _steps.Count - 1 && CanLeave()) ShowStep(_current + 1);
    }

    /// <summary>모든 단계를 다시 검사한 뒤 만든다 — 문제가 있는 단계로 옮겨 문구를 보인다.</summary>
    private async void OnFinish(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        for (var i = 0; i < _steps.Count; i++)
            if (_steps[i].Validate?.Invoke() is { } problem)
            {
                ShowStep(i);
                StatusText.Text = problem;
                return;
            }

        SetBusy(true);
        StatusText.Text = Loc.T("Wizard_Creating");
        try
        {
            Result = await _finish();
            SetBusy(false);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("Wizard_Failed", ex.Message);
            App.Log($"[만들기] 실패: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }
}
