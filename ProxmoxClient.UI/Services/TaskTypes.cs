using ProxmoxClient.Core.Models;
using ProxmoxClient.UI.Localization;

namespace ProxmoxClient.UI.Services;

/// <summary>작업 종류(qmstart 등)를 읽기 쉬운 이름으로.</summary>
public static class TaskTypes
{
    public static string Describe(PveTask task)
    {
        var name = Name(task.Type);
        return string.IsNullOrEmpty(task.Id) ? name : $"{name} · {task.Id}";
    }

    public static string Name(string type)
    {
        var key = "TaskType_" + type;
        var text = L.T(key);
        return text == key ? type : text;
    }
}
