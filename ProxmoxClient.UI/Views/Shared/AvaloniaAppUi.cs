using System.Runtime.CompilerServices;
using Avalonia.Controls;
using ProxmoxClient.Core.Api.Versioning;
using ProxmoxClient.UI.Services;
using ProxmoxClient.UI.Views.Node;

namespace ProxmoxClient.UI.Views.Shared;

/// <summary>
///     공용 화면 로직(<see cref="IAppUi" />)의 Avalonia 쪽 — 화면 위 대화상자·화면 스택. 주인(Control)은 쓰지 않는다.
/// </summary>
internal sealed class AvaloniaAppUi : IAppUi
{
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255",
        Justification = "공용 코드는 이 어셈블리의 코드에서만 불리므로, 어셈블리가 처음 쓰일 때 한 번 연결하면 된다")]
    internal static void Register()
    {
        AppUi.Current = new AvaloniaAppUi();
        TableFormats.BytesText = bytes => Format.Bytes((long)bytes);
    }

    public Task<IReadOnlyDictionary<string, string>?> FormAsync(object? owner, string title,
        IReadOnlyList<FormField> fields, Func<IReadOnlyDictionary<string, string>, string?>? validate,
        ApiFeature? target)
    {
        return FormDialog.ShowAsync(title, fields, validate, target);
    }

    public IDisposable? BeginWrite(string title, bool confirmed)
    {
        // 실제 서버 변경 직전에 한 번 더 확인한다(WriteConfirmation) — 확인 창에 이 작업 이름이 보인다
        return WriteConfirmation.Begin(title, confirmed);
    }

    public string? ShowView(object? owner, string title, ViewSpec content)
    {
        return TableWindow.ShowModal(owner as Control, title, ViewSpecs.Build(content));
    }

    public string? ShowText(object? owner, string title, string text)
    {
        return TextViewWindow.ShowModal(owner as Control, title, text);
    }

    public Task<bool> ConfirmAsync(object? owner, string text, string title, bool destructive)
    {
        // WPF 의 Yes/No 경고 창과 같다 — 확인해야 진행한다
        return Ask.ConfirmAsync(text, title, destructive);
    }

    public string DestructiveIcon(string icon)
    {
        // 휴지통 아이콘이면 확인 창이 위험 표시(빨간 단추)가 된다(ActionRunner)
        return "IconTrash";
    }
}

/// <summary>공용 화면 설계도(<see cref="ViewSpec" />) → Avalonia 컨트롤.</summary>
public static class ViewSpecs
{
    /// <summary>탐색 창 탭 — 내용은 처음 고를 때 만든다.</summary>
    public static NavTab Tab(NavTabSpec spec)
    {
        return new NavTab
        {
            Id = spec.Id, LabelKey = spec.LabelKey, IconKey = spec.IconKey, Create = () => Build(spec.Create()),
            Requires = spec.Requires
        };
    }

    public static TableTab Build(TableSpec spec)
    {
        return new TableTab(spec.Load, spec.Columns, spec.HintKey, spec.Actions, spec.Filterable,
            spec.Open is { } open ? (row, owner) => open(row, owner) : null);
    }

    public static Control Build(ViewSpec spec)
    {
        return spec switch
        {
            TableSpec t => Build(t),
            TextSpec { TopAligned: true } t => NodeUi.TopAligned(new TextEditTab(t.Load, t.Save, t.HintKey)),
            TextSpec t => new TextEditTab(t.Load, t.Save, t.HintKey),
            SubTabsSpec s => new SubTabsView(s.Tabs.Select(t => new SubTab(t.LabelKey, () => Build(t.Create()),
                t.Requires)).ToList()),
            OptionsSpec o => new Guest.Tabs.OptionsTab(o.Options, o.Load, o.Save, o.Target),
            DeferredSpec d => Deferred(d),
            NativeView n => (Control)n.Create(),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.GetType().Name, null)
        };
    }

    private static ContentControl Deferred(DeferredSpec spec)
    {
        var host = new ContentControl();
        host.Loaded += async (_, _) =>
        {
            if (host.Content is not null) return;
            host.Content = Build(await spec.Create());
        };
        return host;
    }
}
