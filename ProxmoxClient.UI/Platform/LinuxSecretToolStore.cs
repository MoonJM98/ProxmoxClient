using System.Diagnostics;
using System.Runtime.Versioning;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     Linux Secret Service(GNOME 키링·KWallet 등) — libsecret 의 secret-tool 로 읽고 쓴다.
///     비밀값은 표준 입력으로만 넘겨 프로세스 목록에 드러나지 않게 한다.
///     secret-tool 이 없거나 Secret Service 가 없는 환경이면 <see cref="SecretStores" /> 가 메모리 저장소로 대신한다.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxSecretToolStore : ISecretStore
{
    private const string Tool = "secret-tool";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public bool IsPersistent => true;

    public static bool IsAvailable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, Tool)));
    }

    public string? Get(string key)
    {
        var (exit, output) = Run(["lookup", "service", SecretStores.Service, "key", key], null);
        return exit == 0 && output.Length > 0 ? output : null;
    }

    public void Set(string key, string value)
    {
        var (exit, _) = Run(["store", "--label", $"{SecretStores.Service} {key}", "service", SecretStores.Service,
            "key", key], value);
        if (exit != 0) throw new InvalidOperationException("secret-tool store failed.");
    }

    public void Remove(string key)
    {
        Run(["clear", "service", SecretStores.Service, "key", key], null);
    }

    private static (int Exit, string Output) Run(IEnumerable<string> arguments, string? input)
    {
        var info = new ProcessStartInfo(Tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("secret-tool did not start.");
        if (input is not null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill();
            return (-1, string.Empty);
        }

        return (process.ExitCode, output);
    }
}
