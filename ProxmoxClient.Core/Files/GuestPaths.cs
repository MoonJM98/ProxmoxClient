namespace ProxmoxClient.Core.Files;

/// <summary>게스트 POSIX 경로('/' 구분) 다루기 — 링크는 풀지 않는다(게스트 쪽에서 푼다).</summary>
public static class GuestPaths
{
    public static string Combine(string directory, string name)
    {
        return directory.EndsWith('/') ? directory + name : directory + "/" + name;
    }

    public static string Parent(string directory)
    {
        var normal = Normalize(directory);
        var cut = normal.LastIndexOf('/');
        return cut <= 0 ? "/" : normal[..cut];
    }

    /// <summary>링크 대상 — 절대 경로면 그대로, 상대 경로면 담긴 폴더 기준.</summary>
    public static string ResolveLink(string directory, GuestFileEntry link)
    {
        var target = link.LinkTarget ?? string.Empty;
        return Normalize(target.StartsWith('/') ? target : Combine(directory, target));
    }

    /// <summary>"." ".." 과 겹 슬래시를 정리한 절대 경로.</summary>
    public static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return "/" + string.Join('/', parts);
    }

    /// <summary>항목 이름 확인 — 경로 구분자·빈 이름·"."·".." 은 받지 않는다(다른 곳을 건드리지 않게).</summary>
    public static string CheckName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
            throw new GuestFileException(Localization.Res.T("GuestFiles_BadName", name));

        return name;
    }
}
