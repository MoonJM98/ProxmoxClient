using System.Windows.Input;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>관리 목록의 한 줄(데이터센터·노드 관리·게스트 관리 항목) — 등록된 기능에서 만든다.</summary>
public sealed record MenuEntry(string Label, string IconKey, ICommand Command, object? Parameter = null);
