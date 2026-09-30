using System.Windows;
using ProxmoxClient.Core.Api.Versioning;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>하위 탭 하나 — 이름·내용, 그리고 쓰는 API 기능(서버가 못 쓰면 <see cref="SubTabsView" /> 가 뺀다).</summary>
public sealed record SubTab(string LabelKey, Func<UIElement> Create, ApiFeature? Requires = null);
