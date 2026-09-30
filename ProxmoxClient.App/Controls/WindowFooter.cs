using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace ProxmoxClient.App.Controls;

/// <summary>탭 꼬리말(상태 문구·저장 버튼)을 받아 창 아래 닫기 버튼 옆에 보여 주는 창.</summary>
public interface IWindowFooterHost
{
    /// <summary><paramref name="owner" /> 탭이 화면에 올라왔다 — 그 꼬리말을 보인다.</summary>
    void ShowFooter(FrameworkElement owner, FrameworkElement footer);

    /// <summary><paramref name="owner" /> 탭이 화면에서 내려갔다 — 그 꼬리말을 뺀다.</summary>
    void HideFooter(FrameworkElement owner);
}

/// <summary>
///     탭 안의 상태 문구·저장 버튼 줄을 창 꼬리말(닫기 옆)로 옮기는 연결 속성.
///     사용 예: &lt;TextBlock x:Name="StatusText" ic:WindowFooter.Move="True" .../&gt;
///     창이 <see cref="IWindowFooterHost" /> 가 아니면(표 창·대화상자 등) 제자리에 그대로 둔다.
///     옮긴 뒤에는 원래 부모 패널이 화면에 올라오고 내려갈 때마다 창 꼬리말에 넣고 뺀다 — 하위 탭을 바꾸면 꼬리말도 바뀐다.
/// </summary>
public static class WindowFooter
{
    public static readonly DependencyProperty MoveProperty = DependencyProperty.RegisterAttached(
        "Move", typeof(bool), typeof(WindowFooter), new PropertyMetadata(false, OnMoveChanged));

    public static bool GetMove(DependencyObject element)
    {
        return (bool)element.GetValue(MoveProperty);
    }

    public static void SetMove(DependencyObject element, bool value)
    {
        element.SetValue(MoveProperty, value);
    }

    private static void OnMoveChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is FrameworkElement footer && e.NewValue is true) footer.Loaded += OnFirstLoaded;
    }

    private static void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        var footer = (FrameworkElement)sender;
        footer.Loaded -= OnFirstLoaded;
        if (Window.GetWindow(footer) is not IWindowFooterHost host || footer.Parent is not Panel owner) return;

        // Loaded 전파 중에 트리를 바꾸지 않도록 한 박자 늦춘다
        footer.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Detach(host, owner, footer));
    }

    private static void Detach(IWindowFooterHost host, Panel owner, FrameworkElement footer)
    {
        if (!owner.Children.Contains(footer)) return;

        owner.Children.Remove(footer);
        footer.Margin = new Thickness(0);
        // 창으로 옮겨도 바인딩({Binding StatusText} 등)은 원래 탭의 DataContext 를 본다
        footer.SetBinding(FrameworkElement.DataContextProperty,
            new Binding(nameof(FrameworkElement.DataContext)) { Source = owner });
        // 상태 문구만 있는 꼬리말은 비어 있으면 자리를 차지하지 않는다
        if (footer is TextBlock text)
            text.SetBinding(UIElement.VisibilityProperty,
                new Binding(nameof(TextBlock.Text)) { Source = text, Converter = HideWhenEmpty.Instance });

        owner.Loaded += (_, _) => host.ShowFooter(owner, footer);
        owner.Unloaded += (_, _) => host.HideFooter(owner);
        if (owner.IsLoaded) host.ShowFooter(owner, footer);
    }

    private sealed class HideWhenEmpty : IValueConverter
    {
        public static readonly HideWhenEmpty Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
