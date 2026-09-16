using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Views;

/// <summary>VNC·터미널 콘솔 설정 — 값 범위 보정은 <see cref="ConsoleSettings.Normalize" /> 가 담당한다.</summary>
public partial class ConsoleSettingsWindow : Window
{
    private static readonly (string Value, string Label)[] EncodingChoices =
    [
        (nameof(ConsoleSettings.VncEncoding.Tight), "ConsoleSettings_EncTight"),
        (nameof(ConsoleSettings.VncEncoding.Zlib), "ConsoleSettings_EncZlib"),
        (nameof(ConsoleSettings.VncEncoding.Raw), "ConsoleSettings_EncRaw")
    ];

    private static readonly string[] FontChoices =
        ["Cascadia Mono", "Consolas", "D2Coding", "NanumGothicCoding", "Lucida Console", "Courier New"];

    private readonly ConsoleSettingsStore _store = new();
    private bool _saving;

    public ConsoleSettingsWindow(ConsoleSettings current)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);

        ComboChoices.Fill(EncodingBox, EncodingChoices);
        FontBox.ItemsSource = FontChoices;
        RowFontSize.Hint = $"{ConsoleSettings.MinFontSize}–{ConsoleSettings.MaxFontSize}";
        QualitySlider.Minimum = CompressionSlider.Minimum = ConsoleSettings.MinLevel;
        QualitySlider.Maximum = CompressionSlider.Maximum = ConsoleSettings.MaxLevel;

        Apply(current.Normalize());
    }

    /// <summary>저장에 성공한 설정(취소 시 null).</summary>
    public ConsoleSettings? SavedSettings { get; private set; }

    private ConsoleSettings.VncEncoding SelectedEncoding =>
        Enum.TryParse<ConsoleSettings.VncEncoding>(ComboChoices.Selected(EncodingBox), out var encoding)
            ? encoding
            : ConsoleSettings.VncEncoding.Tight;

    private void Apply(ConsoleSettings settings)
    {
        ComboChoices.Select(EncodingBox, settings.Encoding.ToString());
        QualitySlider.Value = settings.QualityLevel;
        CompressionSlider.Value = settings.CompressionLevel;
        ExtendedKeysCheck.IsChecked = settings.UseQemuExtendedKeys;
        SmoothScalingCheck.IsChecked = settings.SmoothScaling;
        FontBox.Text = settings.TerminalFontFamily;
        FontSizeBox.Text = settings.TerminalFontSize.ToString();
        UpdateEncodingRows();
    }

    private void OnEncodingChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEncodingRows();
    }

    /// <summary>품질은 Tight 에서만, 압축 레벨은 Raw 가 아닐 때만 의미가 있다.</summary>
    private void UpdateEncodingRows()
    {
        if (RowQuality is null || RowCompression is null) return;

        RowQuality.IsEnabled = SelectedEncoding == ConsoleSettings.VncEncoding.Tight;
        RowCompression.IsEnabled = SelectedEncoding != ConsoleSettings.VncEncoding.Raw;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saving) return;

        if (!int.TryParse(FontSizeBox.Text, out var fontSize)
            || fontSize is < ConsoleSettings.MinFontSize or > ConsoleSettings.MaxFontSize)
        {
            SetStatus(Loc.T("ConsoleSettingsWindow_M01", ConsoleSettings.MinFontSize, ConsoleSettings.MaxFontSize));
            return;
        }

        var settings = new ConsoleSettings
        {
            Encoding = SelectedEncoding,
            QualityLevel = (int)Math.Round(QualitySlider.Value),
            CompressionLevel = (int)Math.Round(CompressionSlider.Value),
            UseQemuExtendedKeys = ExtendedKeysCheck.IsChecked == true,
            SmoothScaling = SmoothScalingCheck.IsChecked == true,
            TerminalFontFamily = FontBox.Text,
            TerminalFontSize = fontSize
        }.Normalize();

        _saving = true;
        BtnSave.IsEnabled = false;
        try
        {
            await _store.SaveAsync(settings);
            SavedSettings = settings;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log($"[콘솔 설정] 저장 실패: {ex}");
            SetStatus(Loc.T("AppSettingsWindow_M03", ex.Message));
        }
        finally
        {
            _saving = false;
            BtnSave.IsEnabled = true;
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Foreground = (Brush)FindResource("BrushWarn");
        StatusText.Text = text;
    }
}