using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Net.Codecrete.QrCodeGenerator;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>
///     TOTP 등록 창 — 인증 앱으로 찍을 QR 코드(웹 UI 와 같은 otpauth 주소)와, 직접 입력용 비밀 값을 함께 보인다.
/// </summary>
internal sealed class TotpQrWindow : Window
{
    private const int ModulePixels = 6;
    private const int QuietZone = 4;

    private TotpQrWindow(string secret, string uri)
    {
        Style = (Style)Application.Current.FindResource("ThemedWindow");
        Title = Loc.T("DcTfa_SecretTitle");
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = Loc.T("DcTfa_ScanHint"), TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new Border
        {
            Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Image { Source = Render(uri), Stretch = Stretch.None,
                SnapsToDevicePixels = true }
        });
        panel.Children.Add(new TextBlock { Text = Loc.T("DcTfa_SecretManual"), Margin = new Thickness(0, 14, 0, 4) });
        panel.Children.Add(new TextBox { Text = secret, IsReadOnly = true, FontFamily = new FontFamily("Consolas"),
            MaxWidth = 360 });
        var close = new Button { Content = Loc.T("Common_Close"), IsDefault = true, IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 90, Margin = new Thickness(0, 16, 0, 0) };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = panel;
        Loaded += (_, _) => WindowTheme.ApplyDarkTitleBar(this);
    }

    public static void ShowModal(Window? owner, string secret, string uri)
    {
        new TotpQrWindow(secret, uri) { Owner = owner }.ShowDialog();
    }

    /// <summary>QR 모듈을 흰 여백과 함께 검은 점으로 그린 흑백 비트맵(화면 배율과 무관하게 또렷하게 정수 배).</summary>
    internal static BitmapSource Render(string text)
    {
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        var side = (qr.Size + QuietZone * 2) * ModulePixels;
        var pixels = new byte[side * side];
        Array.Fill(pixels, (byte)0xFF);
        for (var y = 0; y < qr.Size; y++)
        for (var x = 0; x < qr.Size; x++)
        {
            if (!qr.GetModule(x, y)) continue;
            for (var dy = 0; dy < ModulePixels; dy++)
            {
                var row = ((y + QuietZone) * ModulePixels + dy) * side + (x + QuietZone) * ModulePixels;
                Array.Fill(pixels, (byte)0, row, ModulePixels);
            }
        }

        var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Gray8, null, pixels, side);
        bitmap.Freeze();
        return bitmap;
    }
}
