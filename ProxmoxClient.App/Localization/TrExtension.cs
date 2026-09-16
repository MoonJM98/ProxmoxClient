using System.Windows.Data;
using System.Windows.Markup;

namespace ProxmoxClient.App.Localization;

/// <summary>
///     XAML 문자열 표기: <c>Text="{loc:Tr GuestListTitle}"</c>.
///     <see cref="Loc" /> 의 인덱서에 OneWay 로 묶어, 언어를 바꾸면 화면 글자가 즉시 바뀐다.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    /// <summary>리소스 키(Strings.resx).</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Current,
            Mode = BindingMode.OneWay
        };

        // Setter.Value 등에서도 쓸 수 있도록 BindingExpression 이 아닌 바인딩 자체를 넘긴다
        return binding.ProvideValue(serviceProvider);
    }
}