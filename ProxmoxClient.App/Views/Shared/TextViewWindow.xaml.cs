using System.Windows;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>긴 글(패키지 변경 기록 등)을 읽기 전용으로 보여 주는 창.</summary>
public partial class TextViewWindow : Window
{
    public TextViewWindow(string title, string text)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Title = title;
        Body.Text = text;
    }

    /// <summary>주인 창 위에 모달로 띄운다. 보기 전용이라 표에서 바뀐 것은 없다고 돌려준다(null).</summary>
    public static string? ShowModal(Window? owner, string title, string text)
    {
        new TextViewWindow(title, text) { Owner = owner }.ShowDialog();
        return null;
    }
}
