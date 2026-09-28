namespace ProxmoxClient.Core.Models;

/// <summary>
///     Proxmox 의 "property string" — 설정 값 안에 든 쉼표 구분 목록.
///     예: <c>local-lvm:vm-100-disk-0,size=32G,ssd=1</c>, <c>virtio=BC:24:11:21:73:9F,bridge=vmbr0</c>,
///     <c>order=1,up=30</c>, <c>1,fstrim_cloned_disks=1</c>.
///     키 없는 항목은 그 형식의 기본 키(defaultKey: file·enabled 등) 값으로 본다.
///     항목 순서를 지켜 두어, 다시 조립해도 서버 값과 모양이 같게 한다.
/// </summary>
public sealed class PropertyString
{
    private readonly List<KeyValuePair<string, string>> _items;

    private PropertyString(List<KeyValuePair<string, string>> items)
    {
        _items = items;
    }

    public IReadOnlyList<KeyValuePair<string, string>> Items => _items;

    public static PropertyString Empty { get; } = new([]);

    /// <summary>
    ///     해석한다. <paramref name="defaultKey" /> 가 있으면 키 없는 항목을 그 키로 본다
    ///     (디스크의 file, 에이전트의 enabled). 네트워크처럼 "모델=MAC" 이 첫 항목인 형식은 그대로 둔다.
    /// </summary>
    public static PropertyString Parse(string? value, string? defaultKey = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return Empty;

        var items = new List<KeyValuePair<string, string>>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            items.Add(eq < 0
                ? new KeyValuePair<string, string>(defaultKey ?? part, defaultKey is null ? string.Empty : part)
                : new KeyValuePair<string, string>(part[..eq], part[(eq + 1)..]));
        }

        return new PropertyString(items);
    }

    public string? this[string key] => _items.FirstOrDefault(kv => kv.Key == key).Value;

    public bool Has(string key)
    {
        return _items.Any(kv => kv.Key == key);
    }

    public string Get(string key, string fallback = "")
    {
        return this[key] ?? fallback;
    }

    /// <summary>켜짐 여부 — 1·on·yes·true 를 켜짐으로 본다. 없으면 <paramref name="fallback" />.</summary>
    public bool IsOn(string key, bool fallback = false)
    {
        return this[key] is { } v ? v is "1" or "on" or "yes" or "true" : fallback;
    }

    /// <summary>
    ///     한 키를 바꾼 새 값을 돌려준다(원본은 그대로). null·빈 값이면 그 키를 뺀다.
    ///     있던 키는 제자리에서 바꾸고, 새 키는 끝에 붙인다.
    /// </summary>
    public PropertyString With(string key, string? value)
    {
        var items = new List<KeyValuePair<string, string>>(_items);
        var index = items.FindIndex(kv => kv.Key == key);
        if (string.IsNullOrEmpty(value))
        {
            if (index >= 0) items.RemoveAt(index);
        }
        else if (index >= 0)
        {
            items[index] = new KeyValuePair<string, string>(key, value);
        }
        else
        {
            items.Add(new KeyValuePair<string, string>(key, value));
        }

        return new PropertyString(items);
    }

    /// <summary>여러 키를 한 번에 바꾼다 — 값이 null·빈 값인 키는 뺀다.</summary>
    public PropertyString With(IEnumerable<KeyValuePair<string, string?>> changes)
    {
        return changes.Aggregate(this, (current, change) => current.With(change.Key, change.Value));
    }

    /// <summary>
    ///     서버에 보낼 문자열로 조립한다. <paramref name="defaultKey" /> 항목은 키 없이 맨 앞에 쓴다
    ///     (서버가 그렇게 돌려주므로 모양을 맞춘다).
    /// </summary>
    public string Format(string? defaultKey = null)
    {
        var head = defaultKey is null ? [] : _items.Where(kv => kv.Key == defaultKey).Select(kv => kv.Value);
        var rest = _items.Where(kv => kv.Key != defaultKey)
            .Select(kv => kv.Value.Length == 0 ? kv.Key : $"{kv.Key}={kv.Value}");
        return string.Join(',', head.Concat(rest));
    }

    public override string ToString()
    {
        return Format();
    }

    /// <summary>
    ///     서버가 이미 풀어서 돌려준 JSON 객체(<c>{"type":"secure","network":"10.0.0.0/24"}</c>)를 다시 서버 입력 형식
    ///     (<c>type=secure,network=10.0.0.0/24</c>)으로 — cluster/options·백업 일정은 GET 은 객체, PUT/POST 는 문자열이다.
    ///     참/거짓은 1/0, 배열은 <c>;</c> 로 잇는다. JSON 객체가 아니면 그대로 돌려준다.
    /// </summary>
    public static string FromJsonObject(string text)
    {
        if (!text.StartsWith('{')) return text;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return text;
            var parts = doc.RootElement.EnumerateObject()
                .Select(p => (p.Name, Value: JsonScalar(p.Value)))
                .Where(p => p.Value is not null)
                .Select(p => $"{p.Name}={p.Value}");
            return string.Join(',', parts);
        }
        catch (System.Text.Json.JsonException)
        {
            return text;
        }
    }

    private static string? JsonScalar(System.Text.Json.JsonElement value)
    {
        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => value.GetString(),
            System.Text.Json.JsonValueKind.True => "1",
            System.Text.Json.JsonValueKind.False => "0",
            System.Text.Json.JsonValueKind.Number => value.GetRawText(),
            System.Text.Json.JsonValueKind.Array => string.Join(';', value.EnumerateArray().Select(JsonScalar)),
            _ => null // 중첩 객체·null 은 이 형식으로 표현할 수 없다
        };
    }
}
