using System.Windows;
using ProxmoxClient.Core.Api.Versioning;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>탐색 창 왼쪽 목록의 한 항목 — 이름·아이콘과 내용을 만드는 방법.</summary>
public sealed class NavTab
{
    public required string Id { get; init; }

    /// <summary>탭 이름 리소스 키 — 현재 언어로 조회한다.</summary>
    public required string LabelKey { get; init; }

    /// <summary>Icons.xaml 의 Geometry 키.</summary>
    public required string IconKey { get; init; }

    /// <summary>처음 고를 때 한 번만 부른다. 이후에는 만든 내용을 다시 쓴다.</summary>
    public required Func<UIElement> Create { get; init; }

    /// <summary>이 탭이 쓰는 API 기능 — 서버가 못 쓰면 탐색 창이 탭을 두지 않는다.</summary>
    public ApiFeature? Requires { get; init; }
}
