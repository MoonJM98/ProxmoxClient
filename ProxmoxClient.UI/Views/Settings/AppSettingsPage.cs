using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Settings;
using ProxmoxClient.Core.Vnc;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Controls;
using ProxmoxClient.UI.Views.Guest.Hardware;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Settings;

/// <summary>
///     앱 설정(WPF AppSettingsWindow) — 일반(새로고침 간격·자동 새로고침·언어·용량 단위), 콘솔(콘솔 설정 열기),
///     SFTP. 저장은 Core <see cref="AppSettingsStore" /> 로 WPF 와 같은 파일에 쓴다. 저장하면 화면을 닫는다.
/// </summary>
public sealed class AppSettingsPage : UserControl
{
    /// <summary>표시 언어 — 번역 파일(App.{코드}.resx)을 추가하면 여기에 항목을 더한다.</summary>
    private static readonly (string Value, string Label)[] LanguageChoices =
    [
        (AppSettings.AutoLanguage, "AppSettings_LangAuto"),
        ("ko", "AppSettings_LangKo"),
        ("en", "AppSettings_LangEn")
    ];

    private static readonly (string Value, string Label)[] ByteUnitChoices =
    [
        (nameof(ByteDisplayUnit.Auto), "AppSettings_ByteAuto"),
        (nameof(ByteDisplayUnit.KB), "KB"),
        (nameof(ByteDisplayUnit.MB), "MB"),
        (nameof(ByteDisplayUnit.GB), "GB"),
        (nameof(ByteDisplayUnit.TB), "TB")
    ];

    private readonly AppSettingsStore _store = new();
    private readonly Func<Task>? _saved;
    private readonly SettingsTabs _tabs;
    private readonly Button _save;
    private readonly Button _close;
    private readonly TextBlock _status = new() { Classes = { "DialogStatus" } };
    private AppSettings _current;

    private TextBox _interval = null!;
    private CheckBox _autoRefresh = null!;
    private ComboBox _language = null!;
    private ComboBox _byteUnit = null!;

    /// <param name="current">지금 저장된 설정.</param>
    /// <param name="saved">저장을 마친 뒤 부를 것(메인 화면이 새 설정을 다시 읽는다). 없어도 된다.</param>
    public AppSettingsPage(AppSettings current, Func<Task>? saved = null)
    {
        _current = current.Normalize();
        _saved = saved;

        var general = BuildGeneral();
        var console = BuildConsole();
        var sftp = BuildSftp();
        _tabs = new SettingsTabs(
        [
            ("Settings_General", general),
            ("MainWindow_09", console),
            ("Settings_Sftp", sftp)
        ], vertical: PlatformInfo.IsDesktop);
        _tabs.Changed += OnTabChanged;

        _save = ActionBar.IconButton(Loc.T("AppSettingsWindow_12"), "IconSave", this);
        _save.Classes.Add("DialogPrimaryButton");
        _save.Click += async (_, _) => await SaveAsync();

        _close = ActionBar.IconButton(Loc.T("AppSettingsWindow_13"), "IconX", this);
        _close.Classes.Add("DialogButton");
        _close.Click += (_, _) => Leave();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(_save);
        buttons.Children.Add(_close);

        var footer = new Border
        {
            Classes = { "DialogFooter" },
            Child = new DockPanel { Children = { buttons, _status } }
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        Content = new DockPanel { Children = { footer, _tabs } };
    }

    private Control BuildGeneral()
    {
        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 10), Spacing = 10 };
        panel.Children.Add(Section("MainWindow_03"));

        _interval = HardwareInput.Numeric(false, PlatformInfo.IsDesktop ? 140 : 0);
        _interval.Text = _current.RefreshIntervalSeconds.ToString();
        panel.Children.Add(HardwareInput.Form(Loc.T("AppSettingsWindow_01"), _interval,
            Loc.T("AppSettingsWindow_M01", AppSettings.MinRefreshIntervalSeconds, AppSettings.MaxRefreshIntervalSeconds)));

        _autoRefresh = new CheckBox { Content = Loc.T("AppSettingsWindow_03"), Margin = new Thickness(0, 6, 0, 0) };
        _autoRefresh.IsChecked = _current.AutoRefreshOnStart;
        panel.Children.Add(HardwareInput.Form(Loc.T("AppSettingsWindow_02"), _autoRefresh));

        panel.Children.Add(Section("AppSettingsWindow_04"));

        _language = ChoiceBox.Create(LanguageChoices, _current.Language, stretch: !PlatformInfo.IsDesktop);
        _language.Width = PlatformInfo.IsDesktop ? 200 : double.NaN;
        panel.Children.Add(HardwareInput.Form(Loc.T("AppSettingsWindow_05"), _language, Loc.T("AppSettingsWindow_06")));

        _byteUnit = ChoiceBox.Create(ByteUnitChoices, _current.ByteUnit.ToString(), stretch: !PlatformInfo.IsDesktop);
        _byteUnit.Width = PlatformInfo.IsDesktop ? 200 : double.NaN;
        panel.Children.Add(HardwareInput.Form(Loc.T("AppSettingsWindow_07"), _byteUnit, Loc.T("AppSettingsWindow_08")));

        return panel;
    }

    private Control BuildConsole()
    {
        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 10), Spacing = 10 };
        panel.Children.Add(Section("MainWindow_09"));

        var open = ActionBar.IconButton(Loc.T("AppSettingsWindow_11"), "IconSettings", this);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Click += async (_, _) => await OpenConsoleSettingsAsync();
        panel.Children.Add(HardwareInput.Form(Loc.T("AppSettingsWindow_09"), open, Loc.T("AppSettingsWindow_10")));
        return panel;
    }

    private static Control BuildSftp()
    {
        // SFTP 접속·계정 편집기(WPF 설정 창의 SFTP 탭과 같은 편집기) — 닫기는 편집기 안의 [닫기]
        var editor = new Files.SftpProfilesEditor(null, allowConnect: false);
        editor.CloseRequested += (_, _) => Leave();
        return editor;
    }

    private void OnTabChanged(int index)
    {
        _save.IsVisible = index == 0; // 저장은 일반 탭에서만(WPF 와 같다)
        _close.IsVisible = index != 2; // SFTP 탭은 편집기에 [닫기]가 있다(닫기 두 개가 되지 않게)
        CompactButton.SetLabel(_close, Loc.T(index == 0 ? "AppSettingsWindow_13" : "Sftp_Close"));
        SetStatus(null);
    }

    private async Task SaveAsync()
    {
        if (!int.TryParse(_interval.Text, out var seconds)
            || seconds is < AppSettings.MinRefreshIntervalSeconds or > AppSettings.MaxRefreshIntervalSeconds)
        {
            SetStatus(Loc.T("AppSettingsWindow_M02", AppSettings.MinRefreshIntervalSeconds,
                AppSettings.MaxRefreshIntervalSeconds));
            return;
        }

        var settings = (_current with
        {
            RefreshIntervalSeconds = seconds,
            AutoRefreshOnStart = _autoRefresh.IsChecked == true,
            ByteUnit = Enum.TryParse<ByteDisplayUnit>(ComboChoices.Selected(_byteUnit), out var unit) ? unit : ByteDisplayUnit.Auto,
            Language = ComboChoices.Selected(_language) is { Length: > 0 } language ? language : AppSettings.AutoLanguage
        }).Normalize();

        _save.IsEnabled = false;
        try
        {
            await _store.SaveAsync(settings);
            _current = settings;
            ByteFormatter.DisplayUnit = settings.ByteUnit;
            if (_saved is not null) await _saved();
            Leave();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus(Loc.T("AppSettingsWindow_M03", ex.Message));
        }
        finally
        {
            _save.IsEnabled = true;
        }
    }

    private static async Task OpenConsoleSettingsAsync()
    {
        var current = await new ConsoleSettingsStore().LoadAsync();
        await AppServices.Navigator.PushViewAsync(Loc.T("ConsoleSettingsWindow_01"),
            new ConsoleSettingsPage(current, ConsoleSettingsTab.Vnc));
    }

    private static void Leave()
    {
        _ = AppServices.Navigator.PopAsync();
    }

    private void SetStatus(string? text)
    {
        _status.Text = text ?? string.Empty;
        _status.Foreground = text is null ? BrushOf("BrushDim") : BrushOf("BrushWarn");
    }

    private static TextBlock Section(string key)
    {
        return new TextBlock { Text = Loc.T(key), Classes = { "FormSection" } };
    }

    private static IBrush? BrushOf(string key)
    {
        return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            ? value as IBrush
            : null;
    }
}
