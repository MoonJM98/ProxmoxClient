using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>
///     입력 대화상자 — WPF FormDialog 와 같은 칸 정의(<see cref="FormField" />)를 받아 화면 위에 띄운다.
///     <see cref="ShowAsync" /> 는 확인하면 값(키 → 문자열), 취소하면 null.
///     확인을 눌러도 서버 변경은 실제 요청 직전에 한 번 더 확인한다(<see cref="WriteConfirmation" />).
/// </summary>
public sealed class FormDialog : UserControl, IDismissible
{
    private const long MaxLoadBytes = 256 * 1024;

    private readonly List<(FormField Field, Func<string> Read)> _inputs = [];
    private readonly List<Control> _advancedRows = [];
    private readonly Func<IReadOnlyDictionary<string, string>, string?>? _validate;
    private readonly StackPanel _fields = new() { Spacing = 10 };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _advanced = new() { Content = L.T("FormDialog_Advanced") };
    private IReadOnlyDictionary<string, string>? _result;

    private FormDialog(string title, IReadOnlyList<FormField> fields,
        Func<IReadOnlyDictionary<string, string>, string?>? validate, ApiFeature? target)
    {
        _validate = validate;
        foreach (var field in Available(fields, target)) _inputs.Add((field, AddField(field)));

        _advanced.IsVisible = _advancedRows.Count > 0;
        _advanced.IsCheckedChanged += (_, _) => ApplyAdvanced();
        ApplyAdvanced();

        var ok = new Button { Content = L.T("Common_Ok"), MinWidth = 88, MinHeight = 40 };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => Submit();
        var cancel = new Button { Content = L.T("Common_Cancel"), MinWidth = 88, MinHeight = 40 };
        cancel.Click += (_, _) => Dismiss();

        Content = new DockPanel
        {
            Children =
            {
                Docked(new TextBlock { Text = title, Classes = { "headline" }, Margin = new Thickness(20, 18, 20, 8) },
                    Dock.Top),
                Docked(new StackPanel
                {
                    Margin = new Thickness(20, 8, 20, 16), Spacing = 10,
                    Children =
                    {
                        _error,
                        new DockPanel
                        {
                            Children =
                            {
                                Docked(new StackPanel
                                {
                                    Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, ok }
                                }, Dock.Right),
                                _advanced
                            }
                        }
                    }
                }, Dock.Bottom),
                new ScrollViewer
                {
                    MaxHeight = 520, Padding = new Thickness(20, 4, 20, 4),
                    Content = _fields
                }
            }
        };
    }

    public static async Task<IReadOnlyDictionary<string, string>?> ShowAsync(string title,
        IReadOnlyList<FormField> fields, Func<IReadOnlyDictionary<string, string>, string?>? validate = null,
        ApiFeature? target = null)
    {
        var dialog = new FormDialog(title, fields, validate, target);
        await AppServices.Dialogs.ShowSheetAsync(dialog);
        return dialog._result;
    }

    public static IReadOnlyList<FormField> Available(IReadOnlyList<FormField> fields, ApiFeature? target)
    {
        return fields.Where(f => f.Requires is not { IsAvailable: false } && (target?.Accepts(f.Key) ?? true))
            .ToList();
    }

    public void Dismiss()
    {
        _result = null;
        AppServices.Dialogs.CloseSheet();
    }

    private static T Docked<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private void ApplyAdvanced()
    {
        foreach (var row in _advancedRows) row.IsVisible = _advanced.IsChecked == true;
    }

    private Func<string> AddField(FormField field)
    {
        var label = L.T(field.LabelKey);
        var group = new StackPanel { Spacing = 4 };
        Func<string> read;
        switch (field.Kind)
        {
            case FormFieldKind.Section:
                group.Children.Add(new TextBlock { Text = label, Classes = { "section" } });
                read = () => string.Empty;
                break;
            case FormFieldKind.Bool:
            {
                var check = new CheckBox { Content = label, IsChecked = field.Initial == "1" };
                group.Children.Add(check);
                read = () => check.IsChecked == true ? "1" : "0";
                break;
            }
            case FormFieldKind.Choice:
            {
                var choices = field.Choices ?? [];
                var combo = new ComboBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = choices.Select(c => L.T(c.Label)).ToList()
                };
                var index = choices.ToList().FindIndex(c => c.Value == field.Initial);
                combo.SelectedIndex = index >= 0 ? index : choices.Count > 0 && field.Initial.Length == 0 ? -1 : -1;
                if (index < 0 && choices.Count > 0 && field.Required) combo.SelectedIndex = 0;
                AddLabeled(group, label, combo);
                read = () => combo.SelectedIndex >= 0 ? choices[combo.SelectedIndex].Value : string.Empty;
                break;
            }
            case FormFieldKind.MultiChoice:
                read = AddMultiChoice(group, label, field);
                break;
            case FormFieldKind.Multiline:
                read = AddMultiline(group, label, field);
                break;
            case FormFieldKind.Password:
            {
                var box = new TextBox { PasswordChar = '•' };
                Avalonia.Input.TextInput.TextInputOptions.SetIsSensitive(box, true);
                AddLabeled(group, label, box);
                read = () => box.Text ?? string.Empty;
                break;
            }
            case FormFieldKind.Text when field.Suggest is not null:
                read = AddSuggest(group, label, field);
                break;
            default:
            {
                var box = new TextBox { Text = field.Initial };
                AddLabeled(group, label, box);
                read = () => field.Trim ? (box.Text ?? string.Empty).Trim() : box.Text ?? string.Empty;
                break;
            }
        }

        if (field.Hint is { Length: > 0 } hint)
            group.Children.Add(new TextBlock { Text = hint, Classes = { "subtle" }, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, FontSize = 12 });

        _fields.Children.Add(group);
        if (field.Advanced) _advancedRows.Add(group);
        return read;
    }

    private static void AddLabeled(Panel group, string label, Control input)
    {
        group.Children.Add(new TextBlock { Text = label, Classes = { "label" } });
        group.Children.Add(input);
    }

    private Func<string> AddSuggest(Panel group, string label, FormField field)
    {
        var box = new AutoCompleteBox { Text = field.Initial, FilterMode = AutoCompleteFilterMode.None, MinimumPrefixLength = 0 };
        var refresh = new Button { Content = L.T("FormDialog_Refresh") };
        refresh.Click += async (_, _) =>
        {
            _error.Text = L.T("FormDialog_Scanning");
            try
            {
                var found = await field.Suggest!(CurrentValues());
                box.ItemsSource = found.Select(f => f.Value).ToList();
                _error.Text = found.Count == 0 ? L.T("FormDialog_NothingFound") : string.Empty;
                if (found.Count > 0) box.IsDropDownOpen = true;
            }
            catch (Exception ex)
            {
                _error.Text = L.T("FormDialog_ScanFailed", ex.Message);
            }
        };
        group.Children.Add(new TextBlock { Text = label, Classes = { "label" } });
        group.Children.Add(new DockPanel { Children = { Docked(refresh, Dock.Right), box } });
        return () => field.Trim ? (box.Text ?? string.Empty).Trim() : box.Text ?? string.Empty;
    }

    private Func<string> AddMultiChoice(Panel group, string label, FormField field)
    {
        var selected = field.Initial.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var choices = (field.Choices ?? []).ToList();
        choices.AddRange(selected.Where(v => choices.All(c => c.Value != v)).Order(StringComparer.Ordinal)
            .Select(v => (v, v)));
        var panel = new WrapPanel();
        var checks = new List<(CheckBox Box, string Value)>();
        foreach (var (value, text) in choices)
        {
            var check = new CheckBox { Content = L.T(text), IsChecked = selected.Contains(value), Margin = new Thickness(0, 0, 12, 0) };
            panel.Children.Add(check);
            checks.Add((check, value));
        }

        if (checks.Count == 0) panel.Children.Add(new TextBlock { Text = L.T("FormDialog_NoChoices"), Opacity = 0.7 });
        AddLabeled(group, label, panel);
        return () => string.Join(",", checks.Where(c => c.Box.IsChecked == true).Select(c => c.Value));
    }

    private Func<string> AddMultiline(Panel group, string label, FormField field)
    {
        var box = new TextBox
        {
            Text = field.Initial, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 120,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace")
        };
        AddLabeled(group, label, box);
        if (field.CanLoadFile)
        {
            var load = new Button { Content = L.T("FormDialog_LoadFile"), HorizontalAlignment = HorizontalAlignment.Left };
            load.Click += async (_, _) => await LoadFileIntoAsync(box);
            group.Children.Add(load);
        }

        return () => field.Trim ? (box.Text ?? string.Empty).Trim() : box.Text ?? string.Empty;
    }

    private async Task LoadFileIntoAsync(TextBox box)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            if (stream.CanSeek && stream.Length > MaxLoadBytes)
            {
                _error.Text = L.T("FormDialog_FileTooLarge");
                return;
            }

            using var reader = new StreamReader(stream);
            box.Text = await reader.ReadToEndAsync();
            _error.Text = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error.Text = L.T("FormDialog_FileReadFailed", ex.Message);
        }
    }

    private Dictionary<string, string> CurrentValues()
    {
        return _inputs.Where(i => i.Field.Kind != FormFieldKind.Section)
            .ToDictionary(i => i.Field.Key, i => i.Read(), StringComparer.Ordinal);
    }

    private void Submit()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (field, read) in _inputs)
        {
            var value = read();
            if (field.Required && value.Length == 0 && field.Kind != FormFieldKind.Section)
            {
                _error.Text = L.T("FormDialog_Required", L.T(field.LabelKey));
                return;
            }

            if (field.Kind != FormFieldKind.Section) values[field.Key] = value;
        }

        if (_validate?.Invoke(values) is { } problem)
        {
            _error.Text = problem;
            return;
        }

        _result = values;
        AppServices.Dialogs.CloseSheet();
    }
}
