using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.ViewModels;

/// <summary>
///     게스트 목록 표시용 보기 — 템플릿 숨기기. <see cref="Guests" /> 는 전체 목록 그대로 두고(선택·조회용),
///     표만 이 보기를 쓴다. 템플릿으로 바꾸거나 되돌리면(IsTemplate 변경) 곧바로 다시 거른다.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private bool _showTemplates = true;

    private ListCollectionView? _guestsView;

    /// <summary>콘솔 버튼·메뉴 — 템플릿은 실행할 수 없으므로 콘솔이 없다.</summary>
    public bool CanOpenConsole => (Permissions?.CanConsole ?? true) && SelectedGuest is not { IsTemplate: true };

    /// <summary>콘솔 종류 ▾ — VM 만 여러 콘솔(그래픽·SPICE·직렬)이 있다. CT 는 터미널 하나.</summary>
    public bool CanChooseConsole => CanOpenConsole && SelectedGuest is { Kind: ResourceKind.Qemu };

    /// <summary>스냅샷 버튼·메뉴 — 템플릿은 스냅샷을 만들 수 없다.</summary>
    public bool CanOpenSnapshots => (Permissions?.CanSnapshot ?? true) && SelectedGuest is not { IsTemplate: true };

    /// <summary>게스트 표가 보는 목록 — 템플릿을 숨기면 템플릿을 뺀다.</summary>
    public ICollectionView GuestsView => _guestsView ??= CreateGuestsView(Guests);

    private ListCollectionView CreateGuestsView(ObservableCollection<PveResource> guests)
    {
        var view = new ListCollectionView(guests)
        {
            Filter = item => ShowTemplates || item is not PveResource { IsTemplate: true },
            IsLiveFiltering = true
        };
        view.LiveFilteringProperties.Add(nameof(PveResource.IsTemplate));
        return view;
    }

    /// <summary>
    ///     새로고침은 같은 게스트 객체를 제자리에서 갱신하므로, 고른 게스트가 템플릿으로 바뀌어도 선택은 그대로다 —
    ///     그 게스트의 변경을 직접 들어 콘솔·스냅샷 버튼 표시를 다시 계산한다.
    /// </summary>
    partial void OnSelectedGuestChanged(PveResource? oldValue, PveResource? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedGuestPropertyChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedGuestPropertyChanged;
    }

    private void OnSelectedGuestPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PveResource.IsTemplate) or null or "")) return;

        OnPropertyChanged(nameof(CanOpenConsole));
        OnPropertyChanged(nameof(CanChooseConsole));
        OnPropertyChanged(nameof(CanOpenSnapshots));
    }

    partial void OnShowTemplatesChanged(bool value)
    {
        _guestsView?.Refresh();
        if (!value && SelectedGuest is { IsTemplate: true }) SelectedGuest = null; // 숨긴 행을 선택한 채 두지 않는다
    }

    partial void OnGuestsChanged(ObservableCollection<PveResource> value)
    {
        _guestsView = null;
        OnPropertyChanged(nameof(GuestsView));
    }
}
