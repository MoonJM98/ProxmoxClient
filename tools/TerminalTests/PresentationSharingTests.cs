using ProxmoxClient.Presentation.Shared;
using ProxmoxClient.UI.Services;

namespace TerminalTests;

/// <summary>공용 화면 로직(ProxmoxClient.Presentation)이 Avalonia 앱 쪽 조각을 쓰는지 — 앱이 넣는 것과 그 동작.</summary>
public static class PresentationSharingTests
{
    /// <summary>UI 어셈블리를 먼저 읽어 모듈 초기화(앱 쪽 조각 넣기)가 돌게 한다.</summary>
    private static void LoadUi()
    {
        _ = Format.Bytes(0);
    }

    public static void AppPartsRegistered(Checker c)
    {
        LoadUi();
        c.Is(AppUi.Current is not null, "Avalonia 가 AppUi 를 넣었다");
        c.Is(AppParts.Current is not null, "Avalonia 가 AppParts 를 넣었다");
        c.Is(AppParts.Current?.SendsDigest == true, "Avalonia 는 digest 를 보낸다");
        c.Equal("IconTrash", AppUi.DestructiveIcon("IconBan"), "되돌릴 수 없는 작업은 휴지통 아이콘(위험 확인)");
    }

    public static void TableBytesUseAppFormat(Checker c)
    {
        LoadUi();
        c.Equal(Format.Bytes(1536), TableFormats.Bytes("1536"), "표의 바이트 열은 앱 형식");
        c.Equal("abc", TableFormats.Bytes("abc"), "숫자가 아니면 그대로");
        c.Is(TableFormats.IsCheck(TableFormats.Flag), "켜짐 열은 체크 아이콘");
    }

    public static void ResizeSizeIsStrictDecimal(Checker c)
    {
        LoadUi();
        c.Equal("+1.5G", AppParts.Current.ResizeSize("1.5", 131072), "소수점 크기");
        c.Equal("+10G", AppParts.Current.ResizeSize(" 10 ", 131072), "앞뒤 공백");
        c.Equal(null, AppParts.Current.ResizeSize("1e3", 131072), "지수 표기는 받지 않는다");
        c.Equal(null, AppParts.Current.ResizeSize("0", 131072), "0 은 받지 않는다");
        c.Equal(null, AppParts.Current.ResizeSize("131073", 131072), "최대보다 크면 받지 않는다");
    }

    public static void SubTabsSpecKeepsRequires(Checker c)
    {
        var spec = new SubTabsSpec([("A", () => new TextSpec(() => Task.FromResult(("x", "")), null, "H"))]);
        c.Equal(1, spec.Tabs.Count, "튜플 목록도 하위 탭 설계도로");
        c.Is(spec.Tabs[0].Requires is null, "튜플 목록은 기능 조건 없음");
        c.Is(spec.Tabs[0].Create() is TextSpec { TopAligned: false }, "글 화면은 기본으로 위쪽 맞춤 없음");
    }
}
