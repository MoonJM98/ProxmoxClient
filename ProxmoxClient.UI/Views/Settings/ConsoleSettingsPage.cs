using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ProxmoxClient.Core.Vnc;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Platform;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Controls;
using ProxmoxClient.UI.Views.Guest.Hardware;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Settings;

/// <summary>콘솔 설정 화면이 처음 보여 줄 탭 — 설정을 연 콘솔 종류(WPF ConsoleSettingsTab 과 같은 순서).</summary>
public enum ConsoleSettingsTab
{
    Vnc,
    Rdp,
    Terminal
}

/// <summary>
///     콘솔 설정(WPF ConsoleSettingsWindow) — VNC · RDP · 터미널 탭. 저장은 Core <see cref="ConsoleSettingsStore" /> 로
///     WPF 와 같은 파일에 쓴다(게스트 파일·SFTP 연결 정보는 건드리지 않는다). 값 범위 보정은 <see cref="ConsoleSettings.Normalize" />.
/// </summary>
public sealed class ConsoleSettingsPage : UserControl
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
        ["JetBrains Mono", "Cascadia Mono", "Consolas", "D2Coding", "NanumGothicCoding", "Lucida Console", "Courier New"];

    private readonly ConsoleSettingsStore _store = new();
    private readonly Action<ConsoleSettings>? _saved;
    private readonly SettingsTabs _tabs;
    private readonly Button _save;
    private readonly TextBlock _status = new() { Classes = { "DialogStatus" } };
    private Control? _quality;
    private Control? _compression;

    private ComboBox _encoding = null!;
    private Slider _qualitySlider = null!;
    private Slider _compressionSlider = null!;
    private CheckBox _extendedKeys = null!;
    private CheckBox _smoothScaling = null!;
    private CheckBox _directRenderVnc = null!;
    private CheckBox _directRenderRdp = null!;
    private CheckBox _clipboardVnc = null!;
    private ComboBox _cursorVnc = null!;
    private CheckBox _rdpDynamic = null!;
    private CheckBox _rdpSmooth = null!;
    private CheckBox _rdpClipboard = null!;
    private ComboBox _cursorRdp = null!;
    private AutoCompleteBox _font = null!;
    private TextBox _fontSize = null!;

    private bool _showingError;

    /// <param name="current">지금 저장된 콘솔 설정.</param>
    /// <param name="tab">처음 보일 탭.</param>
    /// <param name="saved">저장 뒤 부를 것(없어도 된다).</param>
    public ConsoleSettingsPage(ConsoleSettings current, ConsoleSettingsTab tab = ConsoleSettingsTab.Vnc,
        Action<ConsoleSettings>? saved = null)
    {
        _saved = saved;
        var settings = current.Normalize();

        var vnc = BuildVnc(settings, out _quality, out _compression);
        var rdp = BuildRdp(settings);
        var terminal = BuildTerminal(settings);
        _tabs = new SettingsTabs(
        [
            ("ConsoleSettings_TabVnc", vnc),
            ("ConsoleSettings_TabRdp", rdp),
            ("ConsoleSettings_TabTerminal", terminal)
        ], vertical: false);
        _tabs.Changed += OnTabChanged;
        _tabs.SelectedIndex = (int)tab;

        _save = ActionBar.IconButton(Loc.T("AppSettingsWindow_12"), "IconSave", this);
        _save.Classes.Add("DialogPrimaryButton");
        _save.Click += async (_, _) => await SaveAsync();

        var close = ActionBar.IconButton(Loc.T("AppSettingsWindow_13"), "IconX", this);
        close.Classes.Add("DialogButton");
        close.Click += (_, _) => _ = AppServices.Navigator.PopAsync();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        DockPanel.SetDock(buttons, Dock.Right);
        buttons.Children.Add(_save);
        buttons.Children.Add(close);

        var footer = new Border
        {
            Classes = { "DialogFooter" },
            Child = new DockPanel { Children = { buttons, _status } }
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel { Children = { footer, _tabs } };

        UpdateEncodingRows();
        OnTabChanged(_tabs.SelectedIndex);
    }

    /// <summary>VNC 탭 — 인코딩·화질·압축·키 입력·화면 스케일링·GPU 그리기·클립보드·커서.</summary>
    private Control BuildVnc(ConsoleSettings s, out Control quality, out Control compression)
    {
        var panel = new StackPanel { Margin = new Thickness(20, 12, 20, 10), Spacing = 10 };

        _encoding = ChoiceBox.Create(EncodingChoices, s.Encoding.ToString(), stretch: false);
        _encoding.Width = 220;
        _encoding.SelectionChanged += (_, _) => UpdateEncodingRows();
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettingsWindow_03"), _encoding,
            Loc.T("ConsoleSettingsWindow_04")));

        _qualitySlider = Level(s.QualityLevel, out var qualityValue);
        quality = HardwareInput.Form(Loc.T("ConsoleSettingsWindow_05"), qualityValue, Loc.T("ConsoleSettingsWindow_06"));
        panel.Children.Add(quality);

        _compressionSlider = Level(s.CompressionLevel, out var compressionValue);
        compression = HardwareInput.Form(Loc.T("ConsoleSettingsWindow_07"), compressionValue,
            Loc.T("ConsoleSettingsWindow_08"));
        panel.Children.Add(compression);

        _extendedKeys = Check(s.UseQemuExtendedKeys, Loc.T("ConsoleSettingsWindow_11"));
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettingsWindow_09"), _extendedKeys,
            Loc.T("ConsoleSettingsWindow_10")));

        _smoothScaling = Check(s.SmoothScaling, Loc.T("ConsoleSettingsWindow_13"));
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettingsWindow_12"), _smoothScaling));

        _directRenderVnc = Check(s.DirectRendering, Loc.T("ConsoleSettings_DirectRenderOn"));
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettings_DirectRender"), _directRenderVnc,
            Loc.T("ConsoleSettings_DirectRenderHint")));

        _clipboardVnc = Check(s.AutoClipboardSync, Loc.T("ConsoleSettings_ClipboardAuto"));
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettings_Clipboard"), _clipboardVnc,
            Loc.T("ConsoleSettings_ClipboardHint")));

        _cursorVnc = ChoiceBox.Create(CursorChoices, s.LocalCursor.ToString(), stretch: false);
        _cursorVnc.Width = 220;
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettings_LocalCursor"), _cursorVnc,
            Loc.T("ConsoleSettings_LocalCursorHint")));

        // GPU 그리기는 VNC·RDP 가 같은 설정 — 두 탭의 체크를 함께 바꾼다(WPF OnDirectRenderClick)
        _directRenderVnc.IsCheckedChanged += (_, _) => _directRenderRdp.IsChecked = _directRenderVnc.IsChecked;
        return panel;
    }

    /// <summary>RDP 탭 — 해상도 맞춤·클립보드·스케일링·GPU 그리기·커서.</summary>
    private Control BuildRdp(ConsoleSettings s)
    {
        var panel = new StackPanel { Margin = new Thickness(20, 12, 20, 10), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("ConsoleSettings_RdpNote"), TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOf("BrushDim"), Margin = new Thickness(0, 0, 0, 10)
        });

        _rdpDynamic = Check(s.RdpDynamicResolution, Loc.T("ConsoleSettings_RdpResolutionFit"));
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettings_RdpResolution"), _rdpDynamic,
            Loc.T("ConsoleSettings_RdpResolutionHint")));

        _rdpClipboard = Check(s.RdpClipboard, Loc.T("ConsoleSettings_RdpClipboardAuto"));
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettings_Clipboard"), _rdpClipboard,
            Loc.T("ConsoleSettings_RdpClipboardHint")));

        _rdpSmooth = Check(s.RdpSmoothScaling, Loc.T("ConsoleSettingsWindow_13"));
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettingsWindow_12"), _rdpSmooth));

        _directRenderRdp = Check(s.DirectRendering, Loc.T("ConsoleSettings_DirectRenderOn"));
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettings_DirectRender"), _directRenderRdp,
            Loc.T("ConsoleSettings_DirectRenderHint")));
        _directRenderRdp.IsCheckedChanged += (_, _) => _directRenderVnc.IsChecked = _directRenderRdp.IsChecked;

        _cursorRdp = ChoiceBox.Create(CursorChoices, s.RdpLocalCursor.ToString(), stretch: false);
        _cursorRdp.Width = 220;
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettings_LocalCursor"), _cursorRdp,
            Loc.T("ConsoleSettings_RdpCursorHint")));
        return panel;
    }

    /// <summary>터미널 탭 — 글꼴(목록에서 고르거나 직접 입력)·글자 크기.</summary>
    private Control BuildTerminal(ConsoleSettings s)
    {
        var panel = new StackPanel { Margin = new Thickness(20, 12, 20, 10), Spacing = 10 };

        _font = new AutoCompleteBox
        {
            ItemsSource = FontChoices, Text = s.TerminalFontFamily, Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left, FilterMode = AutoCompleteFilterMode.StartsWith
        };
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettingsWindow_15"), _font,
            Loc.T("ConsoleSettingsWindow_16")));

        _fontSize = HardwareInput.Numeric(false, PlatformInfo.IsDesktop ? 140 : 0);
        _fontSize.Text = s.TerminalFontSize.ToString();
        panel.Children.Add(HardwareInput.Form(Loc.T("ConsoleSettingsWindow_17"), _fontSize,
            $"{ConsoleSettings.MinFontSize}–{ConsoleSettings.MaxFontSize}"));
        return panel;
    }

    /// <summary>JPEG 품질·압축 레벨 칸 — 0~9 눈금 슬라이더와 옆의 숫자.</summary>
    private static Slider Level(int value, out Control view)
    {
        var slider = new Slider
        {
            Minimum = ConsoleSettings.MinLevel, Maximum = ConsoleSettings.MaxLevel, TickFrequency = 1,
            IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 1, Value = value, Margin = new Thickness(0, 4, 10, 0)
        };
        var number = new TextBlock { Width = 28, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        number.Text = value.ToString();
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) number.Text = ((int)Math.Round(slider.Value)).ToString();
        };
        view = new DockPanel { Children = { number, slider } };
        DockPanel.SetDock(number, Dock.Right);
        return slider;
    }

    private static CheckBox Check(bool value, string text)
    {
        return new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
    }

    private void OnTabChanged(int index)
    {
        // "인코딩·품질·키 입력은 재연결 시 적용" 은 VNC 설정 안내다
        if (_showingError) return;
        _status.Text = index == (int)ConsoleSettingsTab.Vnc ? Loc.T("ConsoleSettingsWindow_18") : string.Empty;
        _status.Foreground = BrushOf("BrushDim");
    }

    private ConsoleSettings.VncEncoding SelectedEncoding =>
        Enum.TryParse<ConsoleSettings.VncEncoding>(ComboChoices.Selected(_encoding), out var encoding)
            ? encoding
            : ConsoleSettings.VncEncoding.Tight;

    /// <summary>품질은 Tight 에서만, 압축 레벨은 Raw 가 아닐 때만 의미가 있다.</summary>
    private void UpdateEncodingRows()
    {
        if (_quality is null || _compression is null) return;

        _quality.IsEnabled = SelectedEncoding == ConsoleSettings.VncEncoding.Tight;
        _compression.IsEnabled = SelectedEncoding != ConsoleSettings.VncEncoding.Raw;
    }

    private static ConsoleSettings.LocalCursorMode SelectedCursor(ComboBox box, ConsoleSettings.LocalCursorMode fallback)
    {
        return Enum.TryParse<ConsoleSettings.LocalCursorMode>(ComboChoices.Selected(box), out var mode) ? mode : fallback;
    }

    private async Task SaveAsync()
    {
        if (!int.TryParse(_fontSize.Text, out var fontSize)
            || fontSize is < ConsoleSettings.MinFontSize or > ConsoleSettings.MaxFontSize)
        {
            SetStatus(Loc.T("ConsoleSettingsWindow_M01", ConsoleSettings.MinFontSize, ConsoleSettings.MaxFontSize));
            _tabs.SelectedIndex = (int)ConsoleSettingsTab.Terminal; // 잘못된 값이 있는 탭을 보여 준다
            return;
        }

        var settings = new ConsoleSettings
        {
            Encoding = SelectedEncoding,
            QualityLevel = (int)Math.Round(_qualitySlider.Value),
            CompressionLevel = (int)Math.Round(_compressionSlider.Value),
            UseQemuExtendedKeys = _extendedKeys.IsChecked == true,
            SmoothScaling = _smoothScaling.IsChecked == true,
            DirectRendering = _directRenderVnc.IsChecked == true,
            AutoClipboardSync = _clipboardVnc.IsChecked == true,
            LocalCursor = SelectedCursor(_cursorVnc, ConsoleSettings.LocalCursorMode.Both),
            RdpDynamicResolution = _rdpDynamic.IsChecked == true,
            RdpSmoothScaling = _rdpSmooth.IsChecked == true,
            RdpClipboard = _rdpClipboard.IsChecked == true,
            RdpLocalCursor = SelectedCursor(_cursorRdp, ConsoleSettings.LocalCursorMode.Arrow),
            TerminalFontFamily = _font.Text ?? string.Empty,
            TerminalFontSize = fontSize
        }.Normalize();

        _save.IsEnabled = false;
        try
        {
            // 파일 안의 다른 설정(게스트 파일·SFTP 연결 정보)은 저장 시점의 값을 그대로 둔다 — WPF 와 같다
            settings = await _store.UpdateAsync(current => settings with
            {
                ShowHiddenGuestFiles = current.ShowHiddenGuestFiles,
                GuestFileConnections = current.GuestFileConnections,
                SftpAccounts = current.SftpAccounts,
                SftpConnections = current.SftpConnections,
                SftpHostKeys = current.SftpHostKeys,
                FtpCertificates = current.FtpCertificates
            });
            _saved?.Invoke(settings);
            _ = AppServices.Navigator.PopAsync();
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

    private void SetStatus(string text)
    {
        _showingError = true;
        _status.Foreground = BrushOf("BrushWarn");
        _status.Text = text;
    }

    private static IBrush? BrushOf(string key)
    {
        return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var value)
            ? value as IBrush
            : null;
    }
}
