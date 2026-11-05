using CommunityToolkit.Mvvm.Input;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>다시 읽기 명령이 있는 화면 — 데스크톱 상세 창 머리글의 새로 고침 버튼이 쓴다.</summary>
public interface IReloadable
{
    IAsyncRelayCommand ReloadCommand { get; }
}
