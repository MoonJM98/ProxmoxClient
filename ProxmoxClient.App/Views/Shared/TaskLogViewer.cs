using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>작업 로그 창 열기 — 작업 목록(메인 창 작업 패널·작업 기록 탭)의 더블클릭·로그 버튼이 쓴다.</summary>
internal static class TaskLogViewer
{
    /// <summary>작업 로그 창을 모달로 연다(읽기·중지·다운로드 오류는 창 안에 보여 준다).</summary>
    public static void Show(ProxmoxApiClient api, PveTask task, Window? owner)
    {
        new TaskLogWindow(api, task) { Owner = owner }.ShowDialog();
    }

    /// <summary>더블클릭한 곳이 작업 행이면 그 작업(헤더·빈 영역이면 null).</summary>
    public static PveTask? RowTask(MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow) source = VisualTreeHelper.GetParent(source);
        return (source as DataGridRow)?.Item as PveTask;
    }
}
