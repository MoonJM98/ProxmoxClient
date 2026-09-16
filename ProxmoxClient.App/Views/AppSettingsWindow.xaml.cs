using System.IO;
using System.Windows;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Settings;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Views;

/// <summary>앱 전역 설정(자동 새로고침 간격 등). 값 범위 보정은 <see cref="AppSettings.Normalize" /> 가 담당한다.</summary>
public partial class AppSettingsWindow : Window
{
    /// <summary>표시 언어 — 번역 파일(Strings.{코드}.resx)을 추가하면 여기에 항목을 더한다.</summary>
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
    private bool _saving;

    public AppSettingsWindow(AppSettings current)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);

        var settings = current.Normalize();
        RowInterval.Hint =
            Loc.T("AppSettingsWindow_M01", AppSettings.MinRefreshIntervalSeconds,
                AppSettings.MaxRefreshIntervalSeconds);
        IntervalBox.Text = settings.RefreshIntervalSeconds.ToString();
        AutoRefreshCheck.IsChecked = settings.AutoRefreshOnStart;
        ComboChoices.Fill(ByteUnitBox, ByteUnitChoices);
        ComboChoices.Select(ByteUnitBox, settings.ByteUnit.ToString());
        ComboChoices.Fill(LanguageBox, LanguageChoices);
        ComboChoices.Select(LanguageBox, settings.Language);
    }

    /// <summary>저장에 성공한 설정(취소 시 null).</summary>
    public AppSettings? SavedSettings { get; private set; }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saving) return;

        if (!int.TryParse(IntervalBox.Text, out var seconds)
            || seconds is < AppSettings.MinRefreshIntervalSeconds or > AppSettings.MaxRefreshIntervalSeconds)
        {
            StatusText.Text =
                Loc.T("AppSettingsWindow_M02", AppSettings.MinRefreshIntervalSeconds,
                    AppSettings.MaxRefreshIntervalSeconds);
            return;
        }

        var settings = new AppSettings
        {
            RefreshIntervalSeconds = seconds,
            AutoRefreshOnStart = AutoRefreshCheck.IsChecked == true,
            ByteUnit = Enum.TryParse<ByteDisplayUnit>(ComboChoices.Selected(ByteUnitBox), out var unit)
                ? unit
                : ByteDisplayUnit.Auto,
            Language = ComboChoices.Selected(LanguageBox) ?? AppSettings.AutoLanguage
        }.Normalize();

        _saving = true;
        BtnSave.IsEnabled = false;
        try
        {
            await _store.SaveAsync(settings);
            Loc.SetLanguage(settings.Language); // 열려 있는 창의 글자까지 즉시 전환
            SavedSettings = settings;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"[앱 설정] 저장 실패: {ex}");
            StatusText.Text = Loc.T("AppSettingsWindow_M03", ex.Message);
        }
        finally
        {
            _saving = false;
            BtnSave.IsEnabled = true;
        }
    }

    private async void OnOpenConsoleSettings(object sender, RoutedEventArgs e)
    {
        var current = await new ConsoleSettingsStore().LoadAsync();
        new ConsoleSettingsWindow(current) { Owner = this }.ShowDialog();
    }
}