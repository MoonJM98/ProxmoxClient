using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Controls;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>
///     휴대폰 표의 상세 시트 — 카드를 누르면 아래에서 올라온다. 목록 카드는 한눈에 볼 것만 보이므로 여기에는 빠짐없이
///     모든 열(빈 값은 —)과 표에 없는 서버 필드까지 보이고, 이 항목에 할 수 있는 작업을 큰 단추로 둔다
///     (지우기처럼 되돌릴 수 없는 작업은 빨간 글자로 맨 아래). 고른 작업은 <see cref="Chosen" /> 에 남기고 닫힌다.
/// </summary>
public sealed class RowDetailSheet : UserControl, IBottomSheet
{
    /// <summary>시트가 화면을 덮는 최대 비율 — 위에 원래 화면이 조금 보여 시트임을 알 수 있게.</summary>
    private const double MaxSheetFraction = 0.88;

    private readonly Grid _layout;

    public RowDetailSheet(string title, string? status, StatusChip.Tone? tone, IReadOnlyList<(string Label, string Value)> fields,
        IReadOnlyList<(string Key, string Value)> extra, IReadOnlyList<SheetAction> actions)
    {
        var handle = new Border
        {
            Width = 36, Height = 4, CornerRadius = new CornerRadius(2), Background = Brush("BrushBorder"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12)
        };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (!string.IsNullOrEmpty(status))
        {
            var chip = StatusChip.Create(status, full: status.Length <= 40, tone: tone);
            chip.Margin = new Thickness(10, 0, 0, 0);
            chip.MaxWidth = 200;
            DockPanel.SetDock(chip, Dock.Right);
            header.Children.Add(chip);
        }

        header.Children.Add(new SelectableTextBlock
        {
            Text = title, Classes = { "headline" }, TextWrapping = TextWrapping.Wrap, FontSize = 17,
            FontWeight = FontWeight.SemiBold
        });

        var list = new StackPanel { Spacing = 0 };
        foreach (var (label, value) in fields) list.Children.Add(FieldRow(label, value));

        if (extra.Count > 0)
        {
            var raw = new StackPanel();
            foreach (var (key, value) in extra) raw.Children.Add(FieldRow(key, value));
            list.Children.Add(new Expander
            {
                Header = L.T("TableTab_RawFields", extra.Count), Content = raw, Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
        }

        var body = new ScrollViewer
        {
            Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        // 작업 단추: 열기(강조)는 한 줄 전체, 나머지는 두 칸씩(단추가 많아도 정보 칸이 좁아지지 않게),
        // 지우기처럼 되돌릴 수 없는 작업은 맨 아래 한 줄 전체·빨간 글자
        var buttons = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        var normal = actions.Where(a => !a.Primary && !a.Danger).ToList();
        var pairs = new Avalonia.Controls.Primitives.UniformGrid { Columns = normal.Count > 1 ? 2 : 1 };
        foreach (var action in actions.Where(a => a.Primary)) buttons.Children.Add(ActionButton(action));
        for (var i = 0; i < normal.Count; i++)
        {
            var button = ActionButton(normal[i]);
            var left = pairs.Columns == 1 || i % 2 == 0;
            button.Margin = pairs.Columns == 1 ? new Thickness(0, 0, 0, 6) : new Thickness(left ? 0 : 3, 0, left ? 3 : 0, 6);
            pairs.Children.Add(button);
        }

        if (normal.Count > 0) buttons.Children.Add(pairs);
        foreach (var action in actions.Where(a => a.Danger && !a.Primary)) buttons.Children.Add(ActionButton(action));

        var close = new Button
        {
            Content = L.T("Common_Close"), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 48, Margin = new Thickness(0, 6, 0, 0)
        };
        close.Click += (_, _) => Dismiss();
        buttons.Children.Add(close);

        // 손잡이 · 머리 · 정보(남는 높이, 넘치면 스크롤) · 단추(늘 보임)
        _layout = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,Auto,*,Auto"), Margin = new Thickness(16, 10, 16, 12)
        };
        Grid.SetRow(header, 1);
        Grid.SetRow(body, 2);
        Grid.SetRow(buttons, 3);
        _layout.Children.Add(handle);
        _layout.Children.Add(header);
        _layout.Children.Add(body);
        _layout.Children.Add(buttons);
        Content = _layout;

        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is { } top) _layout.MaxHeight = top.Bounds.Height * MaxSheetFraction;
        };
    }

    private Button ActionButton(SheetAction action)
    {
        var button = new Button
        {
            Content = action.Label, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 48, Padding = new Thickness(14, 8)
        };
        if (action.Primary) button.Classes.Add("accent");
        if (action.Danger) button.Foreground = Brush("BrushErr");
        IconAssist.SetIcon(button, Icon(action.IconKey));
        ToolTip.SetTip(button, action.Label);
        button.Click += (_, _) =>
        {
            Chosen = action;
            AppServices.Dialogs.CloseSheet();
        };
        return button;
    }

    /// <summary>고른 작업(닫기·바깥 누름·뒤로 가기면 null).</summary>
    public SheetAction? Chosen { get; private set; }

    public void Dismiss()
    {
        Chosen = null;
        AppServices.Dialogs.CloseSheet();
    }

    private static Control FieldRow(string label, string value)
    {
        var grid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("0.38*,0.62*"), Margin = new Thickness(0, 0, 0, 0),
            MinHeight = 36
        };
        var name = new TextBlock
        {
            Text = label, Foreground = Brush("BrushDim"), TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 10, 8)
        };
        var text = new SelectableTextBlock
        {
            Text = value.Length == 0 ? "—" : value, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 8)
        };
        if (value.Length == 0) text.Foreground = Brush("BrushDim");
        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
        return new Border
        {
            BorderBrush = Brush("BrushBorder"), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid
        };
    }

    internal static IBrush? Brush(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            ? value as IBrush
            : null;

    internal static Geometry? Icon(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
            ? value as Geometry
            : null;
}

/// <summary>상세 시트의 작업 단추 하나 — Primary 는 강조색(열기), Danger 는 빨간 글자로 맨 아래.</summary>
public sealed record SheetAction(string Label, string IconKey, bool Primary, bool Danger, object? Tag);
