using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ProxmoxClient.Core.Models;

/// <summary>
///     새로고침마다 값이 실제로 바뀐 속성만 알리는 목록 행 모델의 기반.
/// </summary>
public abstract class ObservableModel : INotifyPropertyChanged
{
    /// <summary>속성 이름별 이벤트 인자 캐시 — 알림마다 PropertyChangedEventArgs 를 새로 만들지 않는다.</summary>
    private static readonly ConcurrentDictionary<string, PropertyChangedEventArgs> ArgsCache =
        new(StringComparer.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>값이 다를 때만 대입하고 알린다. 바뀌었으면 true(파생 속성 알림에 사용).</summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Raise(propertyName);
        return true;
    }

    protected void Raise(string propertyName)
    {
        if (PropertyChanged is { } handler)
            handler(this, ArgsCache.GetOrAdd(propertyName, static name => new PropertyChangedEventArgs(name)));
    }
}