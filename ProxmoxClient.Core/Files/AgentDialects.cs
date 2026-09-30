using System.Globalization;
using System.Text;

namespace ProxmoxClient.Core.Files;

/// <summary>
///     게스트 OS 별 에이전트 명령 — 프로그램과 인자 목록을 만든다. 목록 출력은 두 OS(와 CT)가 같은 모양이다
///     (<see cref="GuestListFormat" />). 읽기 조각은 base64 글, 올리기 조각은 표준 입력의 base64 를 풀어 이어 붙인다.
/// </summary>
internal interface IAgentDialect
{
    char Separator { get; }

    /// <summary>게스트 이름 비교(Windows 는 대소문자 무시).</summary>
    StringComparer NameComparer { get; }

    string Home { get; }
    bool CanTar { get; }
    string Combine(string directory, string name);
    string Parent(string directory);
    string ResolveLink(string directory, GuestFileEntry link);

    /// <summary>이 OS 에서 쓸 수 없는 이름이면 예외.</summary>
    string CheckName(string name);

    /// <summary>목록 한 페이지 — offset 번째부터 count 개(<see cref="GuestListFormat" /> 레코드를 base64 로).</summary>
    IReadOnlyList<string> List(string directory, int offset, int count);

    /// <summary>offset 바이트부터 count 바이트(파일이 짧으면 그만큼)를 base64 로.</summary>
    IReadOnlyList<string> ReadChunk(string path, long offset, int count);

    IReadOnlyList<string> Truncate(string path);
    IReadOnlyList<string> Append(string path);

    /// <summary>임시 파일을 제자리로 — 덮어쓰면 기존 권한·소유자를 잇는다. 같은 이름의 폴더가 있으면 실패.</summary>
    IReadOnlyList<string> Finish(string temp, string directory, string name);

    IReadOnlyList<string> Remove(string path);
    IReadOnlyList<string> CreateDirectory(string directory, string name);
    IReadOnlyList<string> Rename(string directory, string name, string newName);
    IReadOnlyList<string> Delete(string directory, string name);

    /// <summary>폴더를 tar 임시 파일로 묶고 그 크기를 출력한다(CanTar 일 때만).</summary>
    IReadOnlyList<string> TarToTemp(string path, string temp);

    /// <summary>게스트 쪽 임시 파일 경로.</summary>
    string TempPath(string directory, string id);
}

/// <summary>Linux 등 POSIX 게스트 — sh 와 기본 도구(stat·base64·tail·head·tar, busybox 도 됨). 인자는 $1, $2 … 로.</summary>
internal sealed class PosixAgentDialect : IAgentDialect
{
    /// <summary>
    ///     목록 한 페이지($2 번째부터 $3 개) — GNU find 가 있으면 한 번에(파일마다 프로세스를 띄우지 않는다),
    ///     없으면(busybox) 셸 반복으로 같은 레코드를 만든다. 전체를 base64 한 줄로.
    /// </summary>
    private const string ListScript =
        "cd -- \"$1\" 2>/dev/null || { echo \"not a directory: $1\" >&2; exit 2; }\n"
        + "o=$2; n=$3\n"
        + "if find . -maxdepth 0 -printf '' 2>/dev/null && printf 'a\\0' | tail -z -n +1 >/dev/null 2>&1; then\n"
        + "  find -P . -mindepth 1 -maxdepth 1 -printf '%y/%s/%T@/%m/%u/%g\\0%f\\0%l\\0' "
        + "| tail -z -n +$((o * 3 + 1)) | head -z -n $((n * 3))\n"
        + "else\n"
        + "  i=0\n"
        + "  for f in .* *; do\n"
        + "    case \"$f\" in .|..) continue;; esac\n"
        + "    [ -e \"$f\" ] || [ -L \"$f\" ] || continue\n"
        + "    i=$((i + 1)); [ \"$i\" -le \"$o\" ] && continue; [ \"$i\" -gt $((o + n)) ] && break\n"
        + "    if [ -L \"$f\" ]; then t=l; elif [ -d \"$f\" ]; then t=d; elif [ -f \"$f\" ]; then t=f; else t=o; fi\n"
        + "    m=$(stat -c '%s/%Y/%a/%U/%G' -- \"$f\" 2>/dev/null) || m='0/0///'\n"
        + "    l=; [ \"$t\" = l ] && l=$(readlink -- \"$f\")\n"
        + "    printf '%s/%s\\000%s\\000%s\\000' \"$t\" \"$m\" \"$f\" \"$l\"\n"
        + "  done\n"
        + "fi | base64 | tr -d '\\n'";

    private const string FinishScript =
        "if [ -d \"$2/$3\" ] && [ ! -L \"$2/$3\" ]; then echo \"a folder with that name exists\" >&2; exit 3; fi; "
        + "if [ -f \"$2/$3\" ] && [ ! -L \"$2/$3\" ]; then chmod \"$(stat -c %a -- \"$2/$3\")\" -- \"$1\"; "
        + "chown \"$(stat -c %u:%g -- \"$2/$3\")\" -- \"$1\" 2>/dev/null; "
        + "else chmod 644 -- \"$1\"; chown \"$(stat -c %u:%g -- \"$2\")\" -- \"$1\" 2>/dev/null; fi; "
        + "if [ -L \"$2/$3\" ]; then rm -f -- \"$2/$3\"; fi; mv -f -- \"$1\" \"$2/$3\"";

    public char Separator => '/';
    public StringComparer NameComparer => StringComparer.Ordinal;
    public string Home => "/";
    public bool CanTar => true;

    public string Combine(string directory, string name) => GuestPaths.Combine(directory, name);
    public string Parent(string directory) => GuestPaths.Parent(directory);
    public string ResolveLink(string directory, GuestFileEntry link) => GuestPaths.ResolveLink(directory, link);
    public string CheckName(string name) => GuestPaths.CheckName(name);

    public IReadOnlyList<string> List(string directory, int offset, int count)
    {
        return Sh(ListScript, directory, offset.ToString(CultureInfo.InvariantCulture),
            count.ToString(CultureInfo.InvariantCulture));
    }

    public IReadOnlyList<string> ReadChunk(string path, long offset, int count)
    {
        return Sh("[ -f \"$1\" ] || { echo \"not a regular file: $1\" >&2; exit 2; }; "
                  + "tail -c +\"$2\" -- \"$1\" | head -c \"$3\" | base64 | tr -d '\\n'",
            path, (offset + 1).ToString(), count.ToString());
    }

    /// <summary>임시 파일은 소유자만 읽게(600) — 받는 동안 다른 계정이 보지 못하게.</summary>
    public IReadOnlyList<string> Truncate(string path) => Sh("umask 077; : > \"$1\"", path);

    public IReadOnlyList<string> Append(string path) => Sh("base64 -d >> \"$1\"", path);

    public IReadOnlyList<string> Finish(string temp, string directory, string name)
    {
        return Sh(FinishScript, temp, directory, name);
    }

    public IReadOnlyList<string> Remove(string path) => Sh("rm -f -- \"$1\"", path);

    public IReadOnlyList<string> CreateDirectory(string directory, string name)
    {
        return Sh("mkdir -- \"$1/$2\" || exit 1; chown \"$(stat -c %u:%g -- \"$1\")\" -- \"$1/$2\" 2>/dev/null; exit 0",
            directory, name);
    }

    public IReadOnlyList<string> Rename(string directory, string name, string newName)
    {
        return Sh("if [ -e \"$1/$3\" ] || [ -L \"$1/$3\" ]; then echo \"already exists\" >&2; exit 3; fi; "
                  + "mv -- \"$1/$2\" \"$1/$3\"", directory, name, newName);
    }

    public IReadOnlyList<string> Delete(string directory, string name) => Sh("rm -rf -- \"$1/$2\"", directory, name);

    public IReadOnlyList<string> TarToTemp(string path, string temp)
    {
        return Sh("umask 077; tar -C \"$(dirname -- \"$1\")\" -cf \"$2\" -- \"$(basename -- \"$1\")\" || exit 1; "
                  + "wc -c < \"$2\"", path, temp);
    }

    public string TempPath(string directory, string id) => Combine(directory, ".pvc-up." + id);

    /// <summary>sh -c 스크립트 — 인자는 $1… 로(인용 없이 그대로 전달).</summary>
    private static IReadOnlyList<string> Sh(string script, params string[] args)
    {
        return ["sh", "-c", script, "sh", .. args];
    }
}

/// <summary>Windows 게스트 — PowerShell(-EncodedCommand). 경로는 PowerShell 작은따옴표 글자로 넣는다.</summary>
internal sealed class WindowsAgentDialect : IAgentDialect
{
    public char Separator => '\\';
    public StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;
    public string Home => "C:\\";
    public bool CanTar => false;

    public string Combine(string directory, string name)
    {
        return directory.EndsWith('\\') ? directory + name : directory + "\\" + name;
    }

    public string Parent(string directory)
    {
        var trimmed = directory.TrimEnd('\\');
        var cut = trimmed.LastIndexOf('\\');
        if (cut < 0) return trimmed.EndsWith(':') ? trimmed + "\\" : directory;

        var parent = trimmed[..cut];
        return parent.EndsWith(':') ? parent + "\\" : parent;
    }

    public string ResolveLink(string directory, GuestFileEntry link)
    {
        var target = link.LinkTarget ?? string.Empty;
        return target.Contains(':') || target.StartsWith(@"\\") ? target : Combine(directory, target);
    }

    /// <summary>Windows 이름 — '\'·':' 도 경로를 바꾸므로 받지 않는다.</summary>
    public string CheckName(string name)
    {
        if (name.Contains('\\') || name.Contains(':'))
            throw new GuestFileException(Localization.Res.T("GuestFiles_BadName", name));

        return GuestPaths.CheckName(name);
    }

    /// <summary>
    ///     목록 한 페이지 — .NET 열거로 offset 번째부터 count 개만 읽고 멈춘다(큰 폴더도 페이지마다 끝까지 돌지 않는다).
    ///     종류/크기/수정(유닉스 초)/Mode(속성 글자 — h·s 는 숨김·시스템) 레코드를 UTF-8 로 모아 base64 로.
    /// </summary>
    public IReadOnlyList<string> List(string directory, int offset, int count)
    {
        var d = L(directory);
        var end = offset + count;
        return Ps($"$d = [IO.DirectoryInfo]::new({d}); "
                  + $"if (-not $d.Exists) {{ throw \"not a directory: \" + {d} }}; "
                  + "$z = [char]0; $sb = [Text.StringBuilder]::new(); $i = 0; "
                  + "foreach ($e in $d.EnumerateFileSystemInfos()) { "
                  + $"if ($i -ge {end}) {{ break }}; if ($i++ -lt {offset}) {{ continue }}; "
                  + "$link = ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0; "
                  + "$t = if ($link) { 'l' } elseif ($e -is [IO.DirectoryInfo]) { 'd' } else { 'f' }; "
                  + "$len = 0; if ($e -is [IO.FileInfo]) { try { $len = $e.Length } catch { } }; "
                  + "$s = [DateTimeOffset]::new($e.LastWriteTimeUtc).ToUnixTimeSeconds(); "
                  // 대상이 없는 링크(정션 등)는 $null — '' + 로 늘 문자열로
                  + "$l = ''; if ($link) { $l = '' + ($e.Target | Select-Object -First 1) }; "
                  + "[void]$sb.Append(\"$t/$len/$s/$($e.Mode)//\").Append($z).Append($e.Name).Append($z)"
                  + ".Append($l).Append($z) }; "
                  + "[Console]::Out.Write([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($sb.ToString())))");
    }

    public IReadOnlyList<string> ReadChunk(string path, long offset, int count)
    {
        return Ps($"$f = [IO.File]::Open({L(path)}, 'Open', 'Read', 'ReadWrite'); try {{ "
                  + $"[void]$f.Seek({offset}, 'Begin'); $b = New-Object byte[] {count}; $r = 0; "
                  + $"while ($r -lt {count}) {{ $n = $f.Read($b, $r, {count} - $r); "
                  + "if ($n -le 0) { break }; $r += $n }; "
                  + "[Console]::Out.Write([Convert]::ToBase64String($b, 0, $r)) } finally { $f.Close() }");
    }

    public IReadOnlyList<string> Truncate(string path) => Ps($"[IO.File]::WriteAllBytes({L(path)}, [byte[]]@())");

    public IReadOnlyList<string> Append(string path)
    {
        return Ps("$b = [Convert]::FromBase64String([Console]::In.ReadToEnd().Trim()); "
                  + $"$f = [IO.File]::Open({L(path)}, 'Append', 'Write'); try {{ $f.Write($b, 0, $b.Length) }} "
                  + "finally { $f.Close() }");
    }

    /// <summary>
    ///     덮어쓰기는 File.Replace(원본 권한·속성 유지, 실패해도 원본이 남는다), 새 파일은 Move.
    ///     백업 경로는 [NullString]::Value — PowerShell 은 $null 을 문자열 인자에 "" 로 넘겨 "경로 형식" 오류가 난다.
    /// </summary>
    public IReadOnlyList<string> Finish(string temp, string directory, string name)
    {
        return Ps($"$dst = [IO.Path]::Combine({L(directory)}, {L(name)}); "
                  + "if (Test-Path -LiteralPath $dst -PathType Container) { throw 'a folder with that name exists' }; "
                  + $"if (Test-Path -LiteralPath $dst) {{ [IO.File]::Replace({L(temp)}, $dst, [NullString]::Value) }} "
                  + $"else {{ [IO.File]::Move({L(temp)}, $dst) }}");
    }

    public IReadOnlyList<string> Remove(string path)
    {
        return Ps($"Remove-Item -LiteralPath {L(path)} -Force -ErrorAction SilentlyContinue");
    }

    public IReadOnlyList<string> CreateDirectory(string directory, string name)
    {
        return Ps($"[void][IO.Directory]::CreateDirectory([IO.Path]::Combine({L(directory)}, {L(name)}))");
    }

    /// <summary>
    ///     이름 바꾸기 — Windows 는 대소문자를 가리지 않으므로 대소문자만 바꾸면(a.txt → A.txt) 새 이름이 "이미 있다"고
    ///     나온다. 그때는 막지 않고, 폴더는 임시 이름을 거쳐 옮긴다(.NET 이 같은 경로로 보고 거부하므로).
    /// </summary>
    public IReadOnlyList<string> Rename(string directory, string name, string newName)
    {
        return Ps($"$src = [IO.Path]::Combine({L(directory)}, {L(name)}); "
                  + $"$dst = [IO.Path]::Combine({L(directory)}, {L(newName)}); "
                  + $"$same = [string]::Equals({L(name)}, {L(newName)}, 'OrdinalIgnoreCase'); "
                  + "if ((Test-Path -LiteralPath $dst) -and -not $same) "
                  + "{ [Console]::Error.WriteLine('already exists'); exit 3 }; "
                  + "$i = Get-Item -LiteralPath $src -Force; "
                  + "if (-not $i.PSIsContainer) { [IO.File]::Move($src, $dst) } "
                  + "elseif ($same) { $t = [IO.Path]::Combine("
                  + $"{L(directory)}, '.pvc-mv.' + [guid]::NewGuid().ToString('N')); "
                  + "[IO.Directory]::Move($src, $t); [IO.Directory]::Move($t, $dst) } "
                  + "else { [IO.Directory]::Move($src, $dst) }");
    }

    /// <summary>
    ///     지우기 — 링크(정션·심볼릭 링크)는 링크만 지운다. 폴더는 .NET 재귀 삭제(안의 정션을 따라 들어가지 않는다 —
    ///     PowerShell 5.1 Remove-Item -Recurse 는 정션 대상까지 지울 수 있다).
    /// </summary>
    public IReadOnlyList<string> Delete(string directory, string name)
    {
        return Ps($"$p = [IO.Path]::Combine({L(directory)}, {L(name)}); $i = Get-Item -LiteralPath $p -Force; "
                  + "if ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) { $i.Delete() } "
                  + "elseif ($i.PSIsContainer) { [IO.Directory]::Delete($p, $true) } else { [IO.File]::Delete($p) }");
    }

    public IReadOnlyList<string> TarToTemp(string path, string temp) => throw new NotSupportedException();

    public string TempPath(string directory, string id) => Combine(directory, ".pvc-up." + id);

    /// <summary>PowerShell 작은따옴표 글자 — 안의 ' 는 '' 로.</summary>
    private static string L(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>
    ///     PowerShell 한 줄 — 오류는 글로 표준 오류에(직렬화된 CLIXML 이 아니라), 종료 코드 1. 스크립트는 UTF-16 base64 로
    ///     넘겨 따옴표·한글 경로가 명령 줄에서 깨지지 않게 한다.
    /// </summary>
    private static IReadOnlyList<string> Ps(string script)
    {
        var full = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; "
                   + "[Console]::OutputEncoding = [Text.Encoding]::UTF8; "
                   + $"try {{ {script} }} catch {{ [Console]::Error.WriteLine($_.Exception.Message); exit 1 }}";
        return ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(full))];
    }
}
