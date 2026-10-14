using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxmoxClient.App.Localization;
using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.App.Views;

/// <summary>콘솔 설정 창에서 처음 보일 탭 — 설정을 연 콘솔 종류.</summary>
public enum ConsoleSettingsTab
{
    Vnc,
    Rdp,
    Terminal
}

/// <summary>
///     VNC·RDP·터미널 콘솔 설정(탭) — 값 범위 보정은 <see cref="ConsoleSettings.Normalize" /> 가 담당한다.
///     콘솔 창에서 열면 그 콘솔 종류의 탭으로 열린다.
/// </summary>
public partial class ConsoleSettingsWindow : Window
{
    private static readonly (string Value, string Label)[] EncodingChoices =
    [
        (nameof(ConsoleSettings.VncEncoding.Tight), "ConsoleSettings_EncTight"),
        (nameof(ConsoleSettings.VncEncoding.Zlib), "ConsoleSettings_EncZlib"),
        (nameof(ConsoleSettings.VncEncoding.Raw), "ConsoleSettings_EncRaw")
    ];

    private static readonly (string Value, string Label)[] CursorChoices =
    [
        (nameof(ConsoleSettings.LocalCursorMode.Both), "ConsoleSettings_CursorBoth"),
        (nameof(ConsoleSettings.LocalCursorMode.Arrow), "ConsoleSettings_CursorArrow"),
        (nameof(ConsoleSettings.LocalCursorMode.Dot), "ConsoleSettings_CursorDot"),
        (nameof(ConsoleSettings.LocalCursorMode.Hidden), "ConsoleSettings_CursorHidden")
    ];

    private static readonly string[] FontChoices =
        ["Cascadia Mono", "Consolas", "D2Coding", "NanumGothicCoding", "Lucida Console", "Courier New"];

    private readonly ConsoleSettingsStore _store = new();

    /// <summary>하단에 저장 오류를 보이는 중 — 탭을 바꿔도 탭 안내로 덮지 않는다.</summary>
    private bool _showingError;

    private bool _saving;

    /// <summary>콘솔 옆에 따로 띄웠다(<see cref="ShowBeside" />) — 모달이 아니라 취소 버튼이 직접 닫아야 한다.</summary>
    private bool _modeless;

    public ConsoleSettingsWindow(ConsoleSettings current, ConsoleSettingsTab tab = ConsoleSettingsTab.Vnc)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);

        ComboChoices.Fill(EncodingBox, EncodingChoices);
        ComboChoices.Fill(CursorBox, CursorChoices);
        ComboChoices.Fill(RdpCursorBox, CursorChoices);
        FontBox.ItemsSource = FontChoices;
        RowFontSize.Hint = $"{ConsoleSettings.MinFontSize}–{ConsoleSettings.MaxFontSize}";
        QualitySlider.Minimum = CompressionSlider.Minimum = ConsoleSettings.MinLevel;
        QualitySlider.Maximum = CompressionSlider.Maximum = ConsoleSettings.MaxLevel;

        Apply(current.Normalize());
        TabBar.SelectedIndex = (int)tab;
    }

    /// <summary>저장에 성공한 설정(취소 시 null).</summary>
    public ConsoleSettings? SavedSettings { get; private set; }

    /// <summary>저장했다(창은 곧 닫힌다) — 콘솔 옆에 따로 띄운 창은 이것으로 결과를 받는다.</summary>
    public event Action<ConsoleSettings>? Saved;

    /// <summary>
    ///     콘솔·터미널 창 옆에 따로 연다 — 콘솔을 가리거나 막지 않게(설정을 보며 콘솔을 계속 쓸 수 있다). 콘솔 창에 딸려
    ///     있어 콘솔을 닫으면 함께 닫힌다. 그 창에 이미 열려 있으면 앞으로 가져온다.
    /// </summary>
    public static void ShowBeside(Window owner, ConsoleSettings current, ConsoleSettingsTab tab,
        Action<ConsoleSettings> saved)
    {
        if (owner.OwnedWindows.OfType<ConsoleSettingsWindow>().FirstOrDefault() is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        var window = new ConsoleSettingsWindow(current, tab)
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.Manual,
            _modeless = true
        };
        BesideOwner.Place(window, owner, false);
        window.Saved += saved;
        window.Show();
    }

    private ConsoleSettings.VncEncoding SelectedEncoding =>
        Enum.TryParse<ConsoleSettings.VncEncoding>(ComboChoices.Selected(EncodingBox), out var encoding)
            ? encoding
            : ConsoleSettings.VncEncoding.Tight;

    /// <summary>GPU 그리기는 VNC·RDP 가 같은 설정 — 두 탭의 체크를 함께 바꾼다.</summary>
    private void OnDirectRenderClick(object sender, RoutedEventArgs e)
    {
        var on = ((CheckBox)sender).IsChecked == true;
        DirectRenderCheck.IsChecked = RdpDirectRenderCheck.IsChecked = on;
    }

    private void Apply(ConsoleSettings settings)
    {
        ComboChoices.Select(EncodingBox, settings.Encoding.ToString());
        QualitySlider.Value = settings.QualityLevel;
        CompressionSlider.Value = settings.CompressionLevel;
        ExtendedKeysCheck.IsChecked = settings.UseQemuExtendedKeys;
        SmoothScalingCheck.IsChecked = settings.SmoothScaling;
        DirectRenderCheck.IsChecked = RdpDirectRenderCheck.IsChecked = settings.DirectRendering;
        ClipboardSyncCheck.IsChecked = settings.AutoClipboardSync;
        ComboChoices.Select(CursorBox, settings.LocalCursor.ToString());
        RdpDynamicCheck.IsChecked = settings.RdpDynamicResolution;
        RdpSmoothCheck.IsChecked = settings.RdpSmoothScaling;
        RdpClipboardCheck.IsChecked = settings.RdpClipboard;
        ComboChoices.Select(RdpCursorBox, settings.RdpLocalCursor.ToString());
        FontBox.Text = settings.TerminalFontFamily;
        FontSizeBox.Text = settings.TerminalFontSize.ToString();
        UpdateEncodingRows();
    }

    /// <summary>고른 탭만 보인다 — 나머지는 자리만 차지한다(Hidden, 창 높이 유지).</summary>
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = Math.Max(0, TabBar.SelectedIndex);
        VncPanel.Visibility = index == (int)ConsoleSettingsTab.Vnc ? Visibility.Visible : Visibility.Hidden;
        RdpPanel.Visibility = index == (int)ConsoleSettingsTab.Rdp ? Visibility.Visible : Visibility.Hidden;
        TerminalPanel.Visibility = index == (int)ConsoleSettingsTab.Terminal ? Visibility.Visible : Visibility.Hidden;
        // "인코딩·품질·키 입력은 재연결 시 적용" 은 VNC 설정 안내다
        if (!_showingError)
            StatusText.Text = index == (int)ConsoleSettingsTab.Vnc ? Loc.T("ConsoleSettingsWindow_18") : string.Empty;
    }

    private static ConsoleSettings.LocalCursorMode SelectedCursor(ComboBox box,
        ConsoleSettings.LocalCursorMode fallback)
    {
        return Enum.TryParse<ConsoleSettings.LocalCursorMode>(ComboChoices.Selected(box), out var mode)
            ? mode
            : fallback;
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

    /// <summary>
    ///     취소 — 따로 띄운 창은 IsCancel 만으로 닫히지 않는다(모달일 때만 DialogResult 로 닫힘). 모달이면 IsCancel 이
    ///     이 처리기 뒤에 닫으므로 여기서는 닫지 않는다.
    /// </summary>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_modeless) Close();
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_saving) return;

        if (!int.TryParse(FontSizeBox.Text, out var fontSize)
            || fontSize is < ConsoleSettings.MinFontSize or > ConsoleSettings.MaxFontSize)
        {
            SetStatus(Loc.T("ConsoleSettingsWindow_M01", ConsoleSettings.MinFontSize, ConsoleSettings.MaxFontSize));
            TabBar.SelectedIndex = (int)ConsoleSettingsTab.Terminal; // 잘못된 값이 있는 탭을 보여 준다
            return;
        }

        var settings = new ConsoleSettings
        {
            Encoding = SelectedEncoding,
            QualityLevel = (int)Math.Round(QualitySlider.Value),
            CompressionLevel = (int)Math.Round(CompressionSlider.Value),
            UseQemuExtendedKeys = ExtendedKeysCheck.IsChecked == true,
            SmoothScaling = SmoothScalingCheck.IsChecked == true,
            DirectRendering = DirectRenderCheck.IsChecked == true,
            AutoClipboardSync = ClipboardSyncCheck.IsChecked == true,
            LocalCursor = SelectedCursor(CursorBox, ConsoleSettings.LocalCursorMode.Both),
            RdpDynamicResolution = RdpDynamicCheck.IsChecked == true,
            RdpSmoothScaling = RdpSmoothCheck.IsChecked == true,
            RdpClipboard = RdpClipboardCheck.IsChecked == true,
            RdpLocalCursor = SelectedCursor(RdpCursorBox, ConsoleSettings.LocalCursorMode.Arrow),
            TerminalFontFamily = FontBox.Text,
            TerminalFontSize = fontSize
        }.Normalize();

        _saving = true;
        BtnSave.IsEnabled = false;
        try
        {
            settings = await _store.UpdateAsync(current => settings with
            {
                ShowHiddenGuestFiles = current.ShowHiddenGuestFiles,
                GuestFileConnections = current.GuestFileConnections,
                SftpAccounts = current.SftpAccounts,
                SftpConnections = current.SftpConnections,
                SftpHostKeys = current.SftpHostKeys,
                FtpCertificates = current.FtpCertificates
            });
            SavedSettings = settings;
            Saved?.Invoke(settings);
            Close(); // 모달(앱 설정에서 연 경우)이든 따로 띄운 창이든 — 부르는 쪽은 SavedSettings·Saved 로 받는다
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
        _showingError = true;
        StatusText.Foreground = (Brush)FindResource("BrushWarn");
        StatusText.Text = text;
    }
}
