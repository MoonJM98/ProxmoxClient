using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App;

/// <summary>다크 테마 메시지 상자 — System.Windows.MessageBox 호환 시그니처.</summary>
public static class ThemedMessageBox
{
    public static MessageBoxResult Show(Window owner, string text, string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.Information)
    {
        return ShowCore(owner, text, title, buttons, image);
    }

    public static MessageBoxResult Show(string text, string title,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.Information)
    {
        return ShowCore(null, text, title, buttons, image);
    }

    private static MessageBoxResult ShowCore(
        Window? owner, string text, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var app = Application.Current ?? throw new InvalidOperationException("Application이 초기화되지 않았습니다.");
        var result = MessageBoxResult.None;
        Window window = null!;

        var iconColorKey = image switch
        {
            MessageBoxImage.Warning or MessageBoxImage.Exclamation => "BrushWarn",
            MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => "BrushErr",
            MessageBoxImage.Question => "BrushAccent",
            _ => "BrushAccent"
        };

        var icon = new TextBlock
        {
            Text = image switch
            {
                MessageBoxImage.Warning or MessageBoxImage.Exclamation => "!",
                MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => "✕",
                MessageBoxImage.Question => "?",
                _ => "i"
            },
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)app.FindResource(iconColorKey),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 14, 0)
        };

        var message = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };

        var okButton = new Button
            { Content = Loc.T("ThemedMessageBox_M01"), MinWidth = 92, Margin = new Thickness(0, 0, 8, 0) };
        okButton.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        okButton.Click += (_, _) =>
        {
            result = MessageBoxResult.OK;
            window.DialogResult = true;
        };

        var yesButton = new Button
            { Content = Loc.T("ThemedMessageBox_M02"), MinWidth = 92, Margin = new Thickness(0, 0, 8, 0) };
        yesButton.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        yesButton.Click += (_, _) =>
        {
            result = MessageBoxResult.Yes;
            window.DialogResult = true;
        };

        var noButton = new Button { Content = Loc.T("ThemedMessageBox_M03"), MinWidth = 92 };
        noButton.Click += (_, _) =>
        {
            result = MessageBoxResult.No;
            window.DialogResult = false;
        };

        Button[] primary = [okButton];
        if (buttons == MessageBoxButton.YesNo)
        {
            primary = [yesButton, noButton];
        }
        else if (buttons == MessageBoxButton.OKCancel)
        {
            var cancel = new Button { Content = Loc.T("AppSettingsWindow_13"), MinWidth = 92 };
            cancel.Click += (_, _) =>
            {
                result = MessageBoxResult.Cancel;
                window.DialogResult = false;
            };
            primary = [okButton, cancel];
        }

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        foreach (var button in primary) buttonRow.Children.Add(button);

        // 가로 StackPanel 은 폭 제한이 없어 긴 메시지가 줄바꿈되지 않고 잘리므로 Grid 사용
        var body = new Grid { Margin = new Thickness(22, 20, 22, 20) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(message, 1);
        body.Children.Add(icon);
        body.Children.Add(message);

        buttonRow.Margin = new Thickness(0);
        var footer = new Border { Child = buttonRow };
        footer.SetResourceReference(FrameworkElement.StyleProperty, "DialogFooter");

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);

        window = new Window
        {
            Style = (Style)app.FindResource("ThemedWindow"),
            Title = title,
            Width = 480,
            MinWidth = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation =
                owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            Content = root
        };
        WindowTheme.ApplyDarkTitleBar(window);
        window.Loaded += (_, _) => primary[0].Focus();
        window.ShowDialog();
        return result;
    }
}