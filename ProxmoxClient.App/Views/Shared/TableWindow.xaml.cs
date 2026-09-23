using System.Windows;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>표(또는 다른 탭 화면) 하나를 따로 띄우는 창 — 선택한 항목의 세부 목록·규칙을 보여 줄 때 쓴다.</summary>
public partial class TableWindow : Window
{
    public TableWindow(string title, UIElement content)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Title = title;
        Body.Content = content;
    }

    /// <summary>주인 창 위에 모달로 띄운다. 보기 전용이라 표에서 바뀐 것은 없다고 돌려준다(null).</summary>
    public static string? ShowModal(Window? owner, string title, UIElement content)
    {
        new TableWindow(title, content) { Owner = owner }.ShowDialog();
        return null;
    }
}
