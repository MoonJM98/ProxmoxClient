using ProxmoxClient.Core.Terminal;

namespace ProxmoxClient.Core.Files;

/// <summary>
///     CT 파일 작업 셸 명령(bash, 노드 root 셸에서 실행) — 경로·이름은 모두 작은따옴표로 인용해 넣는다
///     (명령 전체는 <see cref="HiddenShell" /> 가 base64 로 실어 보내므로 제어 문자도 tty 를 거치지 않는다).
///     작업(op)은 pvc_with 가 CT 루트를 R 에 두고 하위 셸에서 eval 한다. 경로는 pvc_real 이 CT 루트 기준으로 링크를 푼다.
///     /proc/{pid}/root 아래의 절대 링크는 호스트 기준으로 풀리므로 마지막 항목은 따라가지 않는다(mv -T, chown -h).
/// </summary>
internal static class ContainerScripts
{
    /// <summary>
    ///     셸 준비.
    ///     pvc_real — CT 루트 R 안에서 경로의 링크를 모두 풀어 CT 기준 절대 경로를 낸다(없는 끝 항목은 그대로).
    ///     끝의 x 는 $(…) 가 이름 끝 줄바꿈을 지우지 않게 붙인 표지 — 받는 쪽에서 뗀다.
    ///     pvc_with — CT 루트를 R 에 두고 작업(op)을 하위 셸에서 실행. 꺼져 있으면 pct mount → 작업 → 해제
    ///     (끊기거나 취소돼도 EXIT 트랩으로 해제, 마운트 실패는 97). 지난번에 이 앱이 풀지 못한 마운트(표지 파일)는
    ///     먼저 푼다 — 남아 있으면 CT 가 켜지지 않는다.
    /// </summary>
    public const string Setup =
        "pvc_real() { perl -e 'my($r,$p)=@ARGV;my @t=grep{length}split(m{/},$p);my @o;my $n=0;"
        + "while(@t){my $c=shift @t;next if $c eq \".\";if($c eq \"..\"){pop @o;next}"
        + "my $l=readlink($r.\"/\".join(\"/\",@o,$c));if(defined $l){die \"too many links\\n\" if ++$n>40;"
        + "@o=() if $l=~m{^/};unshift @t,grep{length}split(m{/},$l);next}push @o,$c}"
        + "print \"/\".join(\"/\",@o).\"x\";' \"$1\" \"$2\"; }; "
        + "pvc_with() { local v=$1 op=$2 p o; p=$(lxc-info -n \"$v\" -p -H </dev/null 2>/dev/null); "
        + "if [ -n \"$p\" ]; then R=/proc/$p/root; ( eval \"$op\" ); return; fi; "
        + "o=$(cat \"/run/.pvc-mount.$v\" 2>/dev/null); "
        + "if [ -n \"$o\" ] && kill -0 \"$o\" 2>/dev/null; then echo \"busy\" >&2; return 98; fi; "
        + "if [ -n \"$o\" ]; then pct unmount \"$v\" </dev/null >/dev/null 2>&1; rm -f \"/run/.pvc-mount.$v\"; fi; "
        + "( trap 'if [ \"$(cat \"/run/.pvc-mount.$v\" 2>/dev/null)\" = \"$BASHPID\" ]; then "
        + "pct unmount \"$v\" </dev/null >/dev/null 2>&1; rm -f \"/run/.pvc-mount.$v\"; fi' EXIT; "
        + "trap 'exit 129' HUP TERM INT PIPE; echo \"$BASHPID\" > \"/run/.pvc-mount.$v\"; "
        + "pct mount \"$v\" </dev/null >/dev/null || exit 97; R=/var/lib/lxc/$v/rootfs; ( eval \"$op\" ) ); };";

    /// <summary>
    ///     CT 루트의 호스트 UID(변환 오프셋) \0 /etc/passwd \0 /etc/group \0 — 링크는 CT 안 기준으로 풀고
    ///     일반 파일만, 1 MiB 까지(링크로 호스트 파일·장치를 가리켜도 읽지 않는다).
    /// </summary>
    public const string Names = "stat -c %u \"$R/\"; printf '\\0'; for n in passwd group; do "
                                + "f=$(pvc_real \"$R\" \"/etc/$n\") || f=x; f=${f%x}; "
                                + "[ -n \"$f\" ] && [ -f \"$R$f\" ] && head -c 1048576 -- \"$R$f\"; "
                                + "printf '\\0'; done; true";

    /// <summary>
    ///     폴더 목록 — 항목마다 "%y/%s/%T@/%m/%U/%G" \0 이름 \0 링크 대상 \0. 링크는 <see cref="LinkKinds" /> 가
    ///     폴더를 가리키는지 보고 종류를 "ld" 로 바꾼다.
    /// </summary>
    public static string List(string directory)
    {
        return Dir(directory) + "[ -d \"$R$d\" ] || { echo \"not a directory: $d\" >&2; exit 2; }; "
                              + "find -P \"$R$d\" -mindepth 1 -maxdepth 1 -printf '%y/%s/%T@/%m/%U/%G\\0%f\\0%l\\0' | "
                              + LinkKinds;
    }

    /// <summary>
    ///     목록 레코드를 그대로 넘기되 링크는 CT 루트 R 안에서 끝까지 풀어(pvc_real 과 같은 규칙, 40 번까지) 폴더면
    ///     종류를 "ld" 로 — find %Y 는 노드(호스트) 루트 기준으로 절대 링크를 풀어 틀리므로 쓰지 않는다.
    /// </summary>
    private const string LinkKinds =
        "perl -e 'my($r,$d)=@ARGV;$/=chr(0);"
        + "sub real{my @t=grep{length}split(m{/},$_[0]);my(@o,$n);while(@t){my $c=shift @t;next if $c eq q{.};"
        + "if($c eq q{..}){pop @o;next}my $l=readlink($r.q{/}.join(q{/},@o,$c));"
        + "if(defined $l){return if ++$n>40;@o=() if $l=~m{^/};unshift @t,grep{length}split(m{/},$l);next}"
        + "push @o,$c}q{/}.join(q{/},@o)}"
        + "while(defined(my $m=<STDIN>)){my $f=<STDIN>;my $l=<STDIN>;last unless defined $l;"
        + "if($m=~m{^l/}){my $p=real($d.q{/}.substr($f,0,-1));$m=q{ld}.substr($m,1) if defined $p&&-d($r.$p)}"
        + "print $m,$f,$l}' \"$R\" \"$d\"";

    /// <summary>파일 내용 — 링크면 CT 루트 기준으로 따라간다.</summary>
    public static string ReadFile(string path)
    {
        return Real("f", path) + "[ -f \"$R$f\" ] || { echo \"not a regular file: $f\" >&2; exit 2; }; "
                               + "cat -- \"$R$f\"";
    }

    /// <summary>폴더를 tar 로(안의 링크는 링크 그대로).</summary>
    public static string TarDirectory(string path)
    {
        // dirname·basename 은 $(…) 가 이름 끝 줄바꿈을 지우므로 셸 문자열 연산으로 나눈다
        return Real("f", path) + "[ \"$f\" != / ] || exit 2; p=${f%/*}; [ -n \"$p\" ] || p=/; "
                               + "cd -- \"$R$p\" && tar -cf - -- \"${f##*/}\"";
    }

    /// <summary>
    ///     올리기 — 입력('#' + base64 줄)을 바로 풀어 CT 안 임시 파일(600)로 받고, 덮어쓰면 기존 권한·소유자를,
    ///     새 파일이면 644·담긴 폴더 소유자를 준 뒤 제자리로 옮긴다(mv -T: 같은 이름의 링크는 따라가지 않고 바꾼다).
    ///     앞 단계에서 실패하면 남은 입력 줄은 '#' 로 시작해 셸에서 주석이 된다.
    /// </summary>
    public static string Upload(int vmid, string directory, string name, long length, string id)
    {
        var dst = "\"$R$d/\"" + Q(name);
        var op = Dir(directory)
                 + $"t=\"$R$d/.pvc-up.{id}\"; "
                 + $"if [ -d {dst} ] && [ ! -L {dst} ]; then echo \"a folder with that name exists\" >&2; exit 3; fi; "
                 + $"( umask 077; head -c {HiddenShell.EncodedLength(length)} | tr -d '#' | base64 -d > \"$t\" ) "
                 + "|| { rm -f -- \"$t\"; exit 1; }; "
                 + $"if [ -f {dst} ] && [ ! -L {dst} ]; then chmod --reference={dst} -- \"$t\"; "
                 + $"chown --reference={dst} -- \"$t\"; "
                 + "else chmod 644 -- \"$t\"; chown --reference=\"$R$d\" -- \"$t\"; fi; "
                 + $"mv -fT -- \"$t\" {dst} || {{ rm -f -- \"$t\"; exit 1; }}";
        return With(vmid, op);
    }

    public static string CreateDirectory(string directory, string name)
    {
        var target = "\"$R$d/\"" + Q(name);
        return Dir(directory) + $"mkdir -- {target} && chown -h --reference=\"$R$d\" -- {target}";
    }

    public static string Rename(string directory, string name, string newName)
    {
        var to = "\"$R$d/\"" + Q(newName);
        return Dir(directory) + $"if [ -e {to} ] || [ -L {to} ]; then echo \"already exists\" >&2; exit 3; fi; "
                              + $"mv -T -- \"$R$d/\"{Q(name)} {to}";
    }

    /// <summary>
    ///     지우기 — 링크면 링크만(따라가지 않는다). 마운트 지점은 거부하고 파일 시스템 경계를 넘지 않는다 — CT 가
    ///     자기 안에 호스트 폴더를 bind 마운트해 두면 호스트 root 권한으로 그 내용을 지우게 되므로.
    /// </summary>
    public static string Delete(string directory, string name)
    {
        var target = "\"$R$d/\"" + Q(name);
        return Dir(directory) + $"if [ ! -L {target} ] && mountpoint -q -- {target}; then "
                              + "echo \"mount point - not deleted\" >&2; exit 4; fi; "
                              + $"rm -rf --one-file-system -- {target}";
    }

    /// <summary>CT 루트를 잡고 op 를 실행하는 명령.</summary>
    public static string With(int vmid, string op)
    {
        return $"pvc_with {vmid} {Q(op)}";
    }

    /// <summary>셸 작은따옴표 인용 — 안의 ' 는 '\'' 로.</summary>
    public static string Q(string value)
    {
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    /// <summary>d = 링크를 푼 폴더(CT 기준).</summary>
    private static string Dir(string directory) => Real("d", directory);

    /// <summary>변수 = 링크를 푼 경로(끝의 표지 x 를 뗀다).</summary>
    private static string Real(string variable, string path)
    {
        return $"{variable}=$(pvc_real \"$R\" {Q(path)}) || exit 1; {variable}=${{{variable}%x}}; ";
    }
}
