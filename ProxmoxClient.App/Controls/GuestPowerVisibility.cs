using System.ComponentModel;
using System.Windows;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.App.Controls;

/// <summary>
///     전원 버튼/메뉴를 게스트 상태에 맞게 표시하는 연결 속성.
///     예) 실행 중이면 [시작] 숨김, 정지 상태면 [종료·정지·재부팅] 숨김.
///     사용: ic:GuestPowerVisibility.Action="Start" ic:GuestPowerVisibility.Guest="{Binding SelectedGuest}"
///     (권한 등 추가 조건은 ic:GuestPowerVisibility.Allowed)
///     게스트 객체의 상태 변경은 약한 이벤트로 구독해, 오래 사는 게스트가 창/메뉴를 붙잡지 않는다.
/// </summary>
public static class GuestPowerVisibility
{
    public static readonly DependencyProperty ActionProperty = DependencyProperty.RegisterAttached(
        "Action", typeof(GuestPowerAction), typeof(GuestPowerVisibility),
        new PropertyMetadata(GuestPowerAction.None, (d, _) => Update(d)));

    public static readonly DependencyProperty GuestProperty = DependencyProperty.RegisterAttached(
        "Guest", typeof(PveResource), typeof(GuestPowerVisibility),
        new PropertyMetadata(null, OnGuestChanged));

    public static readonly DependencyProperty AllowedProperty = DependencyProperty.RegisterAttached(
        "Allowed", typeof(bool), typeof(GuestPowerVisibility),
        new PropertyMetadata(true, (d, _) => Update(d)));

    /// <summary>요소별 구독 핸들러 보관 — 약한 이벤트 관리자가 참조만 하므로 요소가 강하게 붙잡아 둔다.</summary>
    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(EventHandler<PropertyChangedEventArgs>), typeof(GuestPowerVisibility),
        new PropertyMetadata(null));

    public static GuestPowerAction GetAction(DependencyObject element)
    {
        return (GuestPowerAction)element.GetValue(ActionProperty);
    }

    public static void SetAction(DependencyObject element, GuestPowerAction value)
    {
        element.SetValue(ActionProperty, value);
    }

    public static PveResource? GetGuest(DependencyObject element)
    {
        return (PveResource?)element.GetValue(GuestProperty);
    }

    public static void SetGuest(DependencyObject element, PveResource? value)
    {
        element.SetValue(GuestProperty, value);
    }

    public static bool GetAllowed(DependencyObject element)
    {
        return (bool)element.GetValue(AllowedProperty);
    }

    public static void SetAllowed(DependencyObject element, bool value)
    {
        element.SetValue(AllowedProperty, value);
    }

    private static void OnGuestChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var handler = (EventHandler<PropertyChangedEventArgs>?)d.GetValue(HandlerProperty);
        if (handler is null)
        {
            handler = (_, _) => Update(d);
            d.SetValue(HandlerProperty, handler);
        }

        if (e.OldValue is INotifyPropertyChanged oldGuest)
            PropertyChangedEventManager.RemoveHandler(oldGuest, handler, string.Empty);

        if (e.NewValue is INotifyPropertyChanged newGuest)
            // string.Empty = 모든 속성 변경 수신 (새로고침은 CopyFrom 에서 PropertyChanged("") 를 발생)
            PropertyChangedEventManager.AddHandler(newGuest, handler, string.Empty);

        Update(d);
    }

    private static void Update(DependencyObject d)
    {
        if (d is not UIElement element) return;

        var action = GetAction(d);
        if (action == GuestPowerAction.None) return;

        var visible = GetAllowed(d) && GetGuest(d) is { } guest && GuestPowerRules.IsAvailable(action, guest);
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}