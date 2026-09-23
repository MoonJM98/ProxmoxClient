using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>여러 칸을 한 번에 입력받는 대화상자 — 사용자·그룹·권한 추가처럼 필드 목록만 다른 입력에 쓴다.</summary>
public partial class FormDialog : Window
{
    private const double LabelWidth = 110;
    private const double MultilineHeight = 110;

    /// <summary>PEM 인증서·키는 몇 KB 수준 — 이보다 크면 잘못 고른 파일로 본다.</summary>
    private const long MaxLoadBytes = 256 * 1024;

    private readonly List<(FormField Field, Func<string> Read)> _inputs = [];
    private readonly List<FrameworkElement> _advancedRows = [];
    private readonly Func<IReadOnlyDictionary<string, string>, string?>? _validate;

    /// <param name="validate">추가 검사 — 문제가 있으면 보여 줄 문구, 없으면 null.</param>
    public FormDialog(string title, IReadOnlyList<FormField> fields,
        Func<IReadOnlyDictionary<string, string>, string?>? validate = null)
    {
        _validate = validate;
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Title = title;

        foreach (var field in fields)
            _inputs.Add((field, AddField(field)));

        AdvancedCheck.Visibility = _advancedRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyAdvanced();

        Loaded += (_, _) => FieldPanel.Children.OfType<FrameworkElement>().FirstOrDefault()?.MoveFocus(
            new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }

    /// <summary>확인을 눌렀을 때의 값(취소면 null). 빈 칸도 키는 들어 있다.</summary>
    public IReadOnlyDictionary<string, string>? Result { get; private set; }

    /// <summary>칸 하나를 넣는다 — 안내 문구와 함께 한 묶음으로 두어 "고급" 전환 때 통째로 숨긴다.</summary>
    private Func<string> AddField(FormField field)
    {
        var before = FieldPanel.Children.Count;
        var read = AddInput(field);
        var added = FieldPanel.Children.Cast<UIElement>().Skip(before).ToList();

        var group = new StackPanel();
        foreach (var element in added)
        {
            FieldPanel.Children.Remove(element);
            group.Children.Add(element);
        }

        if (field.Hint is { Length: > 0 } hint)
            group.Children.Add(new TextBlock
            {
                Text = hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
                Margin = new Thickness(LabelWidth + 8, -4, 0, 8)
            });

        FieldPanel.Children.Add(group);
        if (field.Advanced) _advancedRows.Add(group);
        return read;
    }

    private void OnAdvancedChanged(object sender, RoutedEventArgs e)
    {
        ApplyAdvanced();
    }

    private void ApplyAdvanced()
    {
        var show = AdvancedCheck.IsChecked == true;
        foreach (var row in _advancedRows)
            row.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private Func<string> AddInput(FormField field)
    {
        var label = Loc.T(field.LabelKey);
        switch (field.Kind)
        {
            case FormFieldKind.Section:
                FieldPanel.Children.Add(new TextBlock
                {
                    Text = label, Style = (Style)FindResource("FormSection"), Margin = new Thickness(0, 10, 0, 6)
                });
                return () => string.Empty;
            case FormFieldKind.Bool:
            {
                var check = new CheckBox
                {
                    Content = label, IsChecked = field.Initial == "1", Margin = new Thickness(0, 4, 0, 8)
                };
                FieldPanel.Children.Add(check);
                return () => check.IsChecked == true ? "1" : "0";
            }
            case FormFieldKind.Choice:
            {
                var combo = new ComboBox();
                ComboChoices.Fill(combo, field.Choices ?? []);
                ComboChoices.Select(combo, field.Initial);
                AddRow(label, combo);
                return () => ComboChoices.Selected(combo);
            }
            case FormFieldKind.Multiline:
                return AddMultiline(label, field);
            case FormFieldKind.MultiChoice:
                return AddMultiChoice(label, field);
            case FormFieldKind.Password:
            {
                var box = new PasswordBox();
                AddRow(label, box);
                return () => box.Password;
            }
            default:
            {
                var box = new TextBox { Text = field.Initial };
                AddRow(label, box);
                return () => field.Trim ? box.Text.Trim() : box.Text;
            }
        }
    }

    private Func<string> AddMultiChoice(string label, FormField field)
    {
        var selected = field.Initial.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var panel = new StackPanel();
        var checks = new List<(CheckBox Box, string Value)>();
        foreach (var (value, text) in field.Choices ?? [])
        {
            var check = new CheckBox
            {
                Content = Loc.T(text), IsChecked = selected.Contains(value), Margin = new Thickness(0, 2, 0, 2)
            };
            panel.Children.Add(check);
            checks.Add((check, value));
        }

        if (checks.Count == 0)
            panel.Children.Add(new TextBlock { Text = Loc.T("FormDialog_NoChoices"), Opacity = 0.7 });

        AddRow(label, panel);
        return () => string.Join(",", checks.Where(c => c.Box.IsChecked == true).Select(c => c.Value));
    }

    private Func<string> AddMultiline(string label, FormField field)
    {
        var box = new TextBox
        {
            Text = field.Initial, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = MultilineHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas")
        };
        var panel = new StackPanel();
        panel.Children.Add(box);
        if (field.CanLoadFile)
        {
            var load = new Button { Content = Loc.T("FormDialog_LoadFile"), Margin = new Thickness(0, 4, 0, 0) };
            load.Click += (_, _) => LoadFileInto(box);
            panel.Children.Add(new DockPanel { LastChildFill = false, Children = { load } });
        }
        AddRow(label, panel);
        return () => field.Trim ? box.Text.Trim() : box.Text;
    }

    /// <summary>사용자가 고른 텍스트 파일을 칸에 넣는다 — 너무 큰 파일은 인증서가 아니므로 거절한다.</summary>
    private void LoadFileInto(TextBox box)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T("FormDialog_PemFilter")
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var info = new System.IO.FileInfo(dialog.FileName);
            if (info.Length > MaxLoadBytes)
            {
                ErrorText.Text = Loc.T("FormDialog_FileTooLarge");
                return;
            }

            box.Text = System.IO.File.ReadAllText(dialog.FileName);
            ErrorText.Text = string.Empty;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorText.Text = Loc.T("FormDialog_FileReadFailed", ex.Message);
        }
    }

    private void AddRow(string label, UIElement input)
    {
        FieldPanel.Children.Add(new FormRow { Label = label, LabelWidth = LabelWidth, Content = input });
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, read) in _inputs)
        {
            var value = read();
            if (field.Required && value.Length == 0)
            {
                ErrorText.Text = Loc.T("FormDialog_Required", Loc.T(field.LabelKey));
                return;
            }

            if (field.Kind != FormFieldKind.Section) values[field.Key] = value;
        }

        if (_validate?.Invoke(values) is { } problem)
        {
            ErrorText.Text = problem;
            return;
        }

        Result = values;
        DialogResult = true;
    }
}
