using Avalonia.Markup.Xaml;

namespace ProxmoxClient.UI.Localization;

/// <summary>XAML 에서 <c>{l:Tr Key}</c> 로 문구를 넣는다.</summary>
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return L.T(Key);
    }
}
