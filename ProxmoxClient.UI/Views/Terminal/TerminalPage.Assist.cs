using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Terminal;

/// <summary>
///     휴대폰 터미널 도우미(사용자 요청) — 키 막대 위에 겹쳐 뜨는 목록:
///     <list type="bullet">
///         <item>[기록] 최근 명령 — 이 서버·대상에서 Enter 로 실행한 명령(최신순, 입력 중인 글자로 거름). 누르면 입력 줄을
///             그 명령으로 바꾼다(실행은 Enter 로 직접).</item>
///         <item>[Tab] 후보 — 하나면 bash 가 그대로 완성, 여럿이면 bash 가 찍은 후보를 목록으로. 누르면 그 후보로 완성.</item>
///     </list>
/// </summary>
public sealed partial class TerminalPage
{
    private TerminalHistory? _history;
    private Border? _assist;
    private TextBlock? _assistTitle;
    private ListBox? _assistList;
    private Action<string>? _assistPick;

    /// <summary>목록 오버레이(처음 쓸 때 만든다) — 터미널 아래쪽, 키 막대 바로 위.</summary>
    private Border BuildAssist()
    {
        _assistTitle = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Content = "✕", Foreground = Brushes.White, Background = Brushes.Transparent, Padding = new Thickness(10, 4),
            HorizontalAlignment = HorizontalAlignment.Right, Focusable = false
        };
        close.Click += (_, _) => HideAssist();
        _assistList = new ListBox
        {
            Background = Brushes.Transparent, Foreground = Brushes.White, MaxHeight = 280, Focusable = false,
            FontFamily = AppFonts.MonoText
        };
        _assistList.SelectionChanged += (_, _) =>
        {
            if (_assistList.SelectedItem is not string picked) return;

            _assistList.SelectedItem = null;
            var pick = _assistPick;
            HideAssist();
            pick?.Invoke(picked);
            _terminal.FocusInput();
        };
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F0262626")), CornerRadius = new CornerRadius(10, 10, 0, 0),
            Padding = new Thickness(8, 4, 8, 6), VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false,
            BorderBrush = new SolidColorBrush(Color.Parse("#40FFFFFF")), BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new DockPanel
            {
                Children =
                {
                    DockTop(new Grid { Children = { _assistTitle, close } }),
                    _assistList
                }
            }
        };

        static Control DockTop(Control c)
        {
            DockPanel.SetDock(c, Dock.Top);
            return c;
        }
    }

    private void ShowAssist(string title, IReadOnlyList<string> items, Action<string> pick)
    {
        _assist ??= InsertAssist();
        _assistTitle!.Text = title;
        _assistList!.ItemsSource = items;
        _assistPick = pick;
        _assist.IsVisible = true;
    }

    private void HideAssist()
    {
        if (_assist is not null) _assist.IsVisible = false;
        _assistPick = null;
    }

    /// <summary>오버레이가 지금 보이는지와 항목(검사용).</summary>
    public IReadOnlyList<string> AssistItems => _assist?.IsVisible == true && _assistList?.ItemsSource is IReadOnlyList<string> list
        ? list
        : [];

    /// <summary>항목 하나를 고른 것처럼(검사용).</summary>
    public void PickAssist(string item)
    {
        if (_assistList is not null) _assistList.SelectedItem = item;
    }

    private Border InsertAssist()
    {
        var assist = BuildAssist();
        _terminalHost.Children.Add(assist);
        return assist;
    }

    /// <summary>[기록] — 최근 명령(입력 중인 글자가 있으면 그 글자가 든 것만).</summary>
    public void ShowHistory()
    {
        if (_history is null) return;

        var typed = TerminalAssist.CommandFrom(TerminalAssist.CursorLine(_terminal.Buffer).Text) ?? string.Empty;
        var items = _history.Items.Where(c => typed.Length == 0 || c.Contains(typed, StringComparison.Ordinal)).ToList();
        if (items.Count == 0)
        {
            AppServices.Dialogs.Toast(L.T("Terminal_HistoryEmpty"));
            return;
        }

        // 줄 끝으로 가서(Ctrl+E) 줄을 지우고(Ctrl+U) 고른 명령을 넣는다 — 실행은 사용자가 Enter 로
        ShowAssist(L.T("Terminal_History"), items, command => _terminal.Send("\x05\x15" + command, false));
    }

    /// <summary>
    ///     [Tab] — bash 완성. 한 번 보내 바뀌지 않으면(후보 여럿) 한 번 더 보내 bash 가 후보를 찍게 하고, 찍힌 후보를 목록으로.
    ///     편집기 등 대체 화면이나 프롬프트를 못 찾으면 그냥 Tab.
    /// </summary>
    public async Task CompleteAsync()
    {
        var buffer = _terminal.Buffer;
        var (line, row) = TerminalAssist.CursorLine(buffer);
        var command = buffer.IsAlternateScreen ? null : TerminalAssist.CommandFrom(line);
        _terminal.Send("\t", false);
        if (command is null) return;

        // readline: 바꾼 게 없는 다음 Tab 이 목록을 찍는다. 첫 Tab 이 공통 앞부분만 채웠으면 그다음 Tab 은 바꾼 게 없어
        // 조용하고, 그다음 Tab 에서 목록이 나온다 — 그래서 최대 세 번까지.
        var quiet = 0;
        for (var tab = 1;; tab++)
        {
            await SettleAsync();
            var (after, afterRow) = TerminalAssist.CursorLine(buffer);
            if (afterRow != row) break; // 목록(또는 다 보일지 묻는 줄)이 찍혔다

            if (after != line)
            {
                // 끝까지 완성됨(파일 뒤 공백·폴더 뒤 '/') — 아니면 공통 앞부분만 채운 것이라 후보가 아직 여럿
                if (TerminalAssist.CursorAfterSpace(buffer) || after.EndsWith('/') || TerminalAssist.CommandFrom(after) is not { } next) return;

                line = after;
                command = next;
                quiet = 0;
            }
            else if (++quiet == 2)
            {
                return; // 후보 없음
            }

            if (tab == 3) return;

            _terminal.Send("\t", false);
        }

        if (TerminalAssist.CursorLine(buffer).Text.Contains("(y or n)", StringComparison.Ordinal))
        {
            _terminal.Send("n", false); // 후보가 너무 많다(bash 가 다 보일지 묻는다) — 목록으로 보이지 않는다
            AppServices.Dialogs.Toast(L.T("Terminal_TooManyCompletions"));
            return;
        }

        var items = TerminalAssist.CompletionsBetween(buffer, row, line);
        if (items.Count == 0) return;

        var word = TerminalAssist.LastWord(command);
        ShowAssist(L.T("Terminal_Completions", items.Count), items,
            item => _terminal.Send(TerminalAssist.Remainder(word, item), false));
    }

    /// <summary>셸 출력이 잠잠해질 때까지(최대 1.5초) — 화면 버전이 150ms 동안 그대로면 끝.</summary>
    private async Task SettleAsync()
    {
        var buffer = _terminal.Buffer;
        var last = buffer.Version;
        var quietSince = DateTime.UtcNow;
        var deadline = DateTime.UtcNow.AddMilliseconds(1500);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(40);
            if (buffer.Version != last)
            {
                last = buffer.Version;
                quietSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - quietSince > TimeSpan.FromMilliseconds(150))
            {
                return;
            }
        }
    }

    private void AttachHistory(TerminalViewModelKey key)
    {
        _history = new TerminalHistory(key.Value);
    }
}

/// <summary>기록 묶음 이름 — 서버(주소:포트) + 대상 경로(노드 셸·CT 콘솔).</summary>
public readonly record struct TerminalViewModelKey(string Value);
