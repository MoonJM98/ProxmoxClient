using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Api;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Create;

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
///     웹 UI 와 같은 단계별 만들기 화면(앱 화면 스택의 한 페이지). 위쪽에 단계 표시, 가운데 입력(스크롤),
///     아래 이전·다음·만들기. 폭 412px 에서도 한 열로 쌓여 잘리지 않는다.
///     마지막 단계에서 <c>finish</c> 를 실행하고, 성공하면 페이지를 닫고 결과를 알린다(실패 문구는 상태 줄에 남긴다).
///     만들기 요청은 평소 통신 경로(WriteConfirmation)를 거치므로 서버로 보내기 전에 사용자 확인을 받는다.
/// </summary>
internal sealed class WizardView : UserControl
{
    private readonly string _title;
    private readonly IReadOnlyList<WizardStep> _steps;
    private readonly Func<Task<string>> _finish;
    private readonly TextBlock _stepCount = new() { Classes = { "subtle" }, FontSize = 13 };
    private readonly TextBlock _stepTitle = new() { Classes = { "headline" }, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _chips = new();
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _page;
    private readonly TextBlock _status = new()
    {
        Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap, IsVisible = false
    };
    private readonly CheckBox _advanced = new() { Content = Loc.T("FormDialog_Advanced") };
    private readonly Button _back = new() { Content = Loc.T("Wizard_Back") };
    private readonly Button _next = new() { Content = Loc.T("Wizard_Next") };
    private readonly Button _create;
    private readonly Button _cancel = new() { Content = Loc.T("AppSettingsWindow_13") };
    private bool _busy;
    private int _current = -1;
    private int _reached;

    public WizardView(string title, IReadOnlyList<WizardStep> steps, Func<Task<string>> finish, Func<Task> load)
    {
        _title = title;
        _steps = steps;
        _finish = finish;

        _page = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch };
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _page
        };
        _create = ActionBar.IconButton(Loc.T("Wizard_Finish"), "IconPlus", this);
        _create.Classes.Add("accent");
        _next.Classes.Add("accent");

        var header = new StackPanel { Spacing = 6, Margin = new Thickness(16, 12, 16, 8) };
        header.Children.Add(_stepCount);
        header.Children.Add(_stepTitle);
        header.Children.Add(_chips);

        var buttons = new WrapPanel();
        _advanced.Margin = new Thickness(0, 0, 14, 8);
        foreach (var button in new[] { _back, _next, _create, _cancel })
            button.Margin = new Thickness(0, 0, 8, 8);
        buttons.Children.Add(_advanced);
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        buttons.Children.Add(_create);
        buttons.Children.Add(_cancel);
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(16, 8, 16, 12) };
        footer.Children.Add(_status);
        footer.Children.Add(buttons);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(header, 0);
        Grid.SetRow(_scroll, 1);
        Grid.SetRow(footer, 2);
        grid.Children.Add(header);
        grid.Children.Add(_scroll);
        grid.Children.Add(footer);
        Content = grid;

        _back.Click += (_, _) =>
        {
            if (!_busy && _current > 0) ShowStep(_current - 1);
        };
        _next.Click += (_, _) =>
        {
            if (!_busy && _current < _steps.Count - 1 && CanLeave()) ShowStep(_current + 1);
        };
        _create.Click += async (_, _) => await FinishAsync();
        _cancel.Click += (_, _) =>
        {
            if (!_busy) _ = AppServices.Navigator.PopAsync();
        };
        _advanced.IsCheckedChanged += (_, _) =>
        {
            if (_current >= 0) Rebuild();
        };

        SetBusy(true);
        SetStatus(Loc.T("Hw_Loading"));
        _ = StartAsync(load);
    }

    /// <summary>처음 열 때 — 노드 목록 등을 읽은 뒤 첫 단계를 보인다(읽는 동안 입력을 막는다).</summary>
    private async Task StartAsync(Func<Task> load)
    {
        string? problem = null;
        try
        {
            await load();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[만들기] 목록 조회 실패: {ex.Message}");
            problem = Loc.T("Wz_LoadFailed", ErrorText.Describe(ex));
        }

        SetBusy(false);
        ShowStep(0);
        if (problem is not null) SetStatus(problem);
    }

    /// <summary>지금 단계를 다시 그린다(다른 칸에 영향을 주는 값이 바뀌었을 때).</summary>
    public void Rebuild()
    {
        if (_current < 0) return;
        var offset = _scroll.Offset;
        Render();
        UpdateChrome();
        _scroll.Offset = offset;
    }

    public void SetStatus(string text)
    {
        _status.Text = text;
        _status.IsVisible = text.Length > 0;
    }

    /// <summary>
    ///     노드 목록을 다시 읽거나 만드는 동안 — 입력·단계 이동을 막는다(끝나기 전 값이 섞이지 않게).
    /// </summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateChrome();
    }

    private void ShowStep(int index)
    {
        _current = index;
        _reached = Math.Max(_reached, index);
        _steps[index].OnShown?.Invoke();
        SetStatus(string.Empty);
        Render();
        UpdateChrome();
    }

    private void Render()
    {
        _page.Children.Clear();
        _steps[_current].Build(new WizardPage(_page, _advanced.IsChecked == true, this));
    }

    /// <summary>단계 표시·단추 상태를 맞춘다 — 지나온 단계만 눌러 돌아갈 수 있다.</summary>
    private void UpdateChrome()
    {
        var idle = !_busy;
        _chips.Children.Clear();
        if (_current < 0)
        {
            _stepCount.Text = string.Empty;
            _stepTitle.Text = string.Empty;
            _page.IsEnabled = _back.IsEnabled = _next.IsEnabled = _create.IsEnabled = _cancel.IsEnabled = false;
            _advanced.IsEnabled = false;
            return;
        }

        var last = _current == _steps.Count - 1;
        _stepCount.Text = Loc.T("Create_StepCount", _current + 1, _steps.Count);
        _stepTitle.Text = Loc.T(_steps[_current].TitleKey);
        for (var i = 0; i < _steps.Count; i++)
        {
            var index = i;
            var current = i == _current;
            var chip = new Button
            {
                Content = $"{i + 1}. {Loc.T(_steps[i].TitleKey)}", Padding = new Thickness(10, 4), FontSize = 13,
                Margin = new Thickness(0, 0, 6, 6), IsEnabled = idle && i <= _reached
            };
            if (current)
            {
                chip.Classes.Add("accent");
            }

            chip.Click += (_, _) => OnChipClicked(index);
            _chips.Children.Add(chip);
        }

        _page.IsEnabled = idle;
        _back.IsEnabled = idle && _current > 0;
        _next.IsVisible = !last;
        _next.IsEnabled = idle;
        _create.IsVisible = last;
        _create.IsEnabled = idle;
        _cancel.IsEnabled = idle;
        _advanced.IsEnabled = idle;
    }

    private void OnChipClicked(int target)
    {
        if (_busy || target == _current) return;
        // 아직 거치지 않은 단계로는 건너뛸 수 없다(앞 단계 검사를 거쳐야 한다)
        if (target > _reached || target > _current && !CanLeave()) return;
        ShowStep(target);
    }

    private bool CanLeave()
    {
        if (_steps[_current].Validate?.Invoke() is not { } problem) return true;
        SetStatus(problem);
        return false;
    }

    /// <summary>모든 단계를 다시 검사한 뒤 만든다 — 문제가 있는 단계로 옮겨 문구를 보인다.</summary>
    private async Task FinishAsync()
    {
        if (_busy) return;
        for (var i = 0; i < _steps.Count; i++)
            if (_steps[i].Validate?.Invoke() is { } problem)
            {
                ShowStep(i);
                SetStatus(problem);
                return;
            }

        SetBusy(true);
        SetStatus(Loc.T("Wizard_Creating"));
        string result;
        try
        {
            // 이름만 알린다 — 확인 창은 POST 직전에 WriteConfirmation 이 띄운다(여기서 미리 확인으로 넘기지 않는다)
            using var flow = WriteConfirmation.Begin(_title);
            result = await _finish();
        }
        catch (WriteCancelledException)
        {
            SetBusy(false);
            SetStatus(string.Empty);
            return;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[만들기] 실패: {ex.Message}");
            SetBusy(false);
            SetStatus(Loc.T("Wizard_Failed", ErrorText.Describe(ex)));
            return;
        }

        await AppServices.Navigator.PopAsync();
        await AppServices.Dialogs.AlertAsync(_title, result);
    }
}
