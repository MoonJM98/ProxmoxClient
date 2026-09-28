using ProxmoxClient.App.Localization;
using static ProxmoxClient.App.Views.Shared.ActionHelpers;

namespace ProxmoxClient.App.Views.Datacenter;

/// <summary>저장소 창 결과 → 서버 요청 값, 입력 검사.</summary>
internal static partial class StorageActions
{
    /// <summary>화면 전용 칸(서버로 보내지 않고 다른 값으로 바꾸는 칸).</summary>
    private static readonly HashSet<string> LocalKeys =
        ["enable", "keep-all", "keep-last", "keep-hourly", "keep-daily", "keep-weekly", "keep-monthly", "keep-yearly"];

    /// <summary>추가(POST storage) — 빈 칸은 보내지 않는다. 사용 끔은 disable=1, 보존은 prune-backups 하나로.</summary>
    internal static Dictionary<string, string> CreateForm(StorageType type, IReadOnlyDictionary<string, string> values)
    {
        var form = NonEmpty(values.Where(kv => !LocalKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
        form["type"] = type.Type;
        ApplyContent(type, values, form);
        if (values.TryGetValue("enable", out var enable) && enable != "1") form["disable"] = "1";
        if (form.TryGetValue("shared", out var shared) && shared != "1") form.Remove("shared");
        foreach (var key in form.Where(kv => kv.Value == "0" && IsFlag(type, kv.Key)).Select(kv => kv.Key).ToList())
            form.Remove(key); // 끈 체크 칸은 서버 기본값(0)이라 보내지 않는다
        if (type.HasRetention && PruneBackups(values) is { Length: > 0 } prune) form["prune-backups"] = prune;
        return form;
    }

    /// <summary>
    ///     수정(PUT storage/{id}) — 비운 칸은 delete 로 기본값에. 단 비밀(암호·키링)은 비우면 그대로 둔다.
    /// </summary>
    internal static Dictionary<string, string> EditForm(StorageType type, IReadOnlyDictionary<string, string> values)
    {
        var form = values.Where(kv => !LocalKeys.Contains(kv.Key) && !kv.Key.StartsWith('_'))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var secret in new[] { "password", "keyring" })
            if (form.TryGetValue(secret, out var v) && v.Length == 0) form.Remove(secret);
        ApplyContent(type, values, form);
        form["disable"] = values.TryGetValue("enable", out var enable) && enable == "1" ? "0" : "1";
        if (type.HasRetention) form["prune-backups"] = PruneBackups(values);
        return UpdateForm(form);
    }

    private static bool IsFlag(StorageType type, string key)
    {
        return type.Fields.Any(f => f.Field.Key == key && f.Field.Kind == Shared.FormFieldKind.Bool);
    }

    private static void ApplyContent(StorageType type, IReadOnlyDictionary<string, string> values,
        Dictionary<string, string> form)
    {
        if (type.FixedContent is { } fixedContent)
            form["content"] = fixedContent;
        else if (type.Type == "iscsi")
            form["content"] = values.TryGetValue("content", out var luns) && luns == "1" ? "images" : "none";
    }

    /// <summary>보존: 모두 보관이면 keep-all=1, 아니면 적은 keep-* 만(모두 비면 빈 값 → 수정 시 삭제 = 기본값).</summary>
    internal static string PruneBackups(IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("keep-all", out var all) && all == "1") return "keep-all=1";
        return string.Join(',', KeepKeys.Where(k => values.TryGetValue(k, out var v) && v.Trim().Length > 0)
            .Select(k => $"{k}={values[k].Trim()}"));
    }

    /// <summary>보존 칸(keep-*)은 비우거나 0 이상의 정수 — 저장소 창과 백업 정리 창이 같이 쓴다.</summary>
    internal static string? ValidateKeep(IReadOnlyDictionary<string, string> values)
    {
        foreach (var key in KeepKeys)
            if (values.TryGetValue(key, out var v) && v.Trim().Length > 0 && !(int.TryParse(v, out var n) && n >= 0))
                return Loc.T("DcStorage_BadKeep");
        return null;
    }

    internal static string? Validate(StorageType type, IReadOnlyDictionary<string, string> values, bool isCreate)
    {
        if (isCreate && !StorageIdPattern().IsMatch(values["storage"])) return Loc.T("StorageHint_Id");
        if (type.FixedContent is null && type.Type != "iscsi" && values["content"].Length == 0)
            return Loc.T("DcStorage_NeedContent");
        if (ValidateKeep(values) is { } badKeep) return badKeep;
        if (values.TryGetValue("max-protected-backups", out var max) && max.Trim().Length > 0
            && !(int.TryParse(max, out var m) && m >= -1))
            return Loc.T("DcStorage_BadKeep");
        if (values.TryGetValue("port", out var port) && port.Length > 0
            && !(int.TryParse(port, out var p) && p is >= 1 and <= 65535))
            return Loc.T("DcStorage_BadPort");
        if (type.Type == "pbs" && values.TryGetValue("username", out var user) && !user.Contains('@'))
            return Loc.T("StorageHint_PbsUser");
        return null;
    }
}
