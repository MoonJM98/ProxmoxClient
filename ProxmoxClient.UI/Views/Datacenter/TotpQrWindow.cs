using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Net.Codecrete.QrCodeGenerator;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Shared;

namespace ProxmoxClient.UI.Views.Datacenter;

/// <summary>
///     TOTP 등록 화면 — 인증 앱으로 찍을 QR 코드(웹 UI 와 같은 otpauth 주소)와, 직접 입력용 비밀 값을 함께 보인다.
///     WPF 의 모달 창 대신 화면 위 시트로 띄우고, 닫을 때까지 기다린다.
/// </summary>
internal sealed class TotpQrWindow : UserControl, IDismissible
{
    private TotpQrWindow(string secret, string uri)
    {
        var close = new Button
        {
            Content = L.T("Common_Close"), MinWidth = 88, MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        close.Click += (_, _) => Dismiss();

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20, 18, 20, 16), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = L.T("DcTfa_SecretTitle"), Classes = { "headline" } },
                    new TextBlock { Text = L.T("DcTfa_ScanHint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new QrCodeView(uri) { HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = L.T("DcTfa_SecretManual"), Classes = { "section" } },
                    new TextBox
                    {
                        Text = secret, IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace")
                    },
                    close
                }
            }
        };
    }

    /// <summary>화면 위에 띄우고 닫힐 때까지 기다린다.</summary>
    public static Task ShowAsync(string secret, string uri)
    {
        return AppServices.Dialogs.ShowSheetAsync(new TotpQrWindow(secret, uri));
    }

    public void Dismiss()
    {
        AppServices.Dialogs.CloseSheet();
    }
}

/// <summary>
///     QR 모듈을 흰 여백과 함께 검은 사각형으로 그리는 컨트롤. 화면 폭에 맞춰 줄어들고(최대 260px), 한 번 만든
///     기하 도형을 다시 쓴다. 비트맵이 아니라서 배율과 무관하게 또렷하다.
/// </summary>
internal sealed class QrCodeView : Control
{
    private const int QuietZone = 4;
    private const double MaxSide = 260;

    private readonly QrCode _qr;
    private StreamGeometry? _modules;

    public QrCodeView(string text)
    {
        _qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(MaxSide, double.IsFinite(availableSize.Width) ? availableSize.Width : MaxSide);
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) return;

        var total = _qr.Size + QuietZone * 2;
        var cell = side / total;
        context.FillRectangle(Brushes.White, new Rect(0, 0, side, side));
        context.DrawGeometry(Brushes.Black, null, Modules(cell));
    }

    /// <summary>검은 모듈 하나를 사각형 하나로 — 한 기하 도형에 모아 그린다.</summary>
    private StreamGeometry Modules(double cell)
    {
        if (_modules is not null && Math.Abs(_cell - cell) < 0.0001) return _modules;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var y = 0; y < _qr.Size; y++)
            for (var x = 0; x < _qr.Size; x++)
            {
                if (!_qr.GetModule(x, y)) continue;
                var left = (x + QuietZone) * cell;
                var top = (y + QuietZone) * cell;
                ctx.BeginFigure(new Point(left, top), true);
                ctx.LineTo(new Point(left + cell, top));
                ctx.LineTo(new Point(left + cell, top + cell));
                ctx.LineTo(new Point(left, top + cell));
                ctx.EndFigure(true);
            }
        }

        _modules = geometry;
        _cell = cell;
        return geometry;
    }

    private double _cell;
}
