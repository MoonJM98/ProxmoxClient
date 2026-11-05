using CommunityToolkit.Mvvm.ComponentModel;
using ProxmoxClient.UI.Localization;
using ProxmoxClient.UI.Services;

namespace ProxmoxClient.UI.ViewModels;

/// <summary>페이지 뷰모델 공통 — 화면 진입·이탈 알림과 오류를 대화상자로 보여 주는 실행 도우미.</summary>
public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty] public partial bool IsBusy { get; protected set; }

    /// <summary>화면 제목(모바일 상단 막대·데스크톱 상세 창 머리글).</summary>
    public virtual string Title => string.Empty;

    /// <summary>페이지가 스택에서 빠졌다 — 이후 알림은 무시한다.</summary>
    public bool IsRemoved { get; private set; }

    internal void NotifyAppearing()
    {
        if (!IsRemoved) OnAppearing();
    }

    internal void NotifyDisappearing(bool removed)
    {
        if (IsRemoved) return;

        IsRemoved = removed;
        OnDisappearing(removed);
    }

    /// <summary>페이지가 화면에 나타났다(처음 쌓일 때와 위 페이지가 닫혀 다시 보일 때 모두).</summary>
    public virtual void OnAppearing()
    {
    }

    /// <summary>페이지가 가려지거나 닫혔다. <paramref name="removed" /> 면 스택에서 빠져 다시 쓰지 않는다.</summary>
    public virtual void OnDisappearing(bool removed)
    {
    }

    /// <summary>작업을 실행하고 실패하면 오류 대화상자를 띄운다. 성공하면 true.</summary>
    protected async Task<bool> TryAsync(Func<Task> action, string? busyText = null)
    {
        using var busy = busyText is null ? null : AppServices.Dialogs.ShowBusy(busyText);
        IsBusy = true;
        try
        {
            await action();
            return true;
        }
        catch (Core.Api.WriteCancelledException)
        {
            return false; // 사용자가 변경 확인을 거절함
        }
        catch (Exception ex)
        {
            busy?.Dispose();
            await AppServices.Dialogs.AlertAsync(L.T("Error_Title"), ErrorText.Describe(ex));
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
