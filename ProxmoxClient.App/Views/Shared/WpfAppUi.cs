using System.Windows;
using ProxmoxClient.Core.Api.Versioning;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>공용 화면 로직(<see cref="IAppUi" />)의 WPF 쪽 — 모달 창, 주인은 <see cref="Window" />.</summary>
internal sealed class WpfAppUi : IAppUi
{
    public Task<IReadOnlyDictionary<string, string>?> FormAsync(object? owner, string title,
        IReadOnlyList<FormField> fields, Func<IReadOnlyDictionary<string, string>, string?>? validate,
        ApiFeature? target)
    {
        var dialog = new FormDialog(title, fields, validate, target) { Owner = owner as Window };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Result : null);
    }

    public IDisposable? BeginWrite(string title, bool confirmed)
    {
        return null;
    }

    public string? ShowView(object? owner, string title, ViewSpec content)
    {
        return TableWindow.ShowModal(owner as Window, title, ViewSpecs.Build(content));
    }

    public string? ShowText(object? owner, string title, string text)
    {
        return TextViewWindow.ShowModal(owner as Window, title, text);
    }

    public Task<bool> ConfirmAsync(object? owner, string text, string title, bool destructive)
    {
        var answer = ThemedMessageBox.Show(owner as Window ?? Application.Current.MainWindow!, text, title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return Task.FromResult(answer == MessageBoxResult.Yes);
    }

    public string DestructiveIcon(string icon)
    {
        return icon;
    }
}

/// <summary>공용 화면 설계도(<see cref="ViewSpec" />) → WPF 컨트롤.</summary>
internal static class ViewSpecs
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

    public static UIElement Build(ViewSpec spec)
    {
        return spec switch
        {
            TableSpec t => Build(t),
            TextSpec t => new TextEditTab(t.Load, t.Save, t.HintKey),
            SubTabsSpec s => new SubTabsView(s.Tabs.Select(t => new SubTab(t.LabelKey, () => Build(t.Create()),
                t.Requires)).ToList()),
            OptionsSpec o => new Guest.Tabs.OptionsTab(o.Options, o.Load, o.Save, o.Target),
            DeferredSpec d => Deferred(d),
            NativeView n => (UIElement)n.Create(),
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.GetType().Name, null)
        };
    }

    private static System.Windows.Controls.ContentControl Deferred(DeferredSpec spec)
    {
        var host = new System.Windows.Controls.ContentControl();
        host.Loaded += async (_, _) =>
        {
            if (host.Content is not null) return;
            host.Content = Build(await spec.Create());
        };
        return host;
    }
}
