using System.Collections.Concurrent;
using System.Text;

namespace ProxmoxClient.Core.Settings;

/// <summary>
///     설정·프로필 파일 안전 쓰기/보존 도우미. 기존 파일은 복구 불가능하게 잃지 않는다.
///     - 쓰기: 고유 임시 파일에 기록 → 디스크 flush → 기존 파일이 있으면 <c>.bak</c> 백업을 남기며 교체(File.Replace).
///     전원 차단 시 0바이트 파일, 실패 시 임시 파일 잔존을 막는다.
///     - 동시 저장: 같은 경로 저장은 프로세스 안에서 순서대로 수행하고(교체 경합으로 저장이 실패하던 문제 방지),
///     다른 프로세스(백신·인덱서)가 잠시 잡고 있으면 짧게 재시도한다.
///     - 손상 보존: 읽기에 실패한 파일을 <c>.corrupt-타임스탬프</c> 사본으로 남겨, 이후 저장이 원본을 덮어도 복구할 수 있게 한다.
/// </summary>
public static class AtomicFile
{
    private const int ReplaceAttempts = 4;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>경로별 저장 직렬화 게이트(Windows 경로는 대소문자 무시). 설정 파일 수만큼만 생긴다.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="keepBackup">
    ///     직전 내용을 <c>.bak</c> 로 남길지. 자격 증명 제거 마이그레이션처럼 이전 내용이 남으면 안 되는 경우에만 false.
    /// </param>
    public static async Task WriteAllTextAsync(string path, string content, bool keepBackup = true,
        CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(path);
        var gate = PathGates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteCoreAsync(fullPath, content, keepBackup, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task WriteCoreAsync(string fullPath, string content, bool keepBackup, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(Utf8NoBom.GetBytes(content), ct).ConfigureAwait(false);
                stream.Flush(true);
            }

            for (var attempt = 1;; attempt++)
                try
                {
                    if (File.Exists(fullPath))
                        File.Replace(tempPath, fullPath, keepBackup ? fullPath + ".bak" : null, true);
                    else
                        File.Move(tempPath, fullPath);

                    return;
                }
                catch (IOException) when (attempt < ReplaceAttempts)
                {
                    // 다른 프로세스가 대상·백업 파일을 잠시 열고 있음 — 잠깐 뒤 재시도(임시 파일은 그대로 유지)
                    await Task.Delay(ReplaceRetryDelay * attempt, ct).ConfigureAwait(false);
                }
        }
        catch
        {
            TryDeleteTemp(tempPath);
            throw;
        }
    }

    /// <summary>
    ///     손상된 파일의 사본을 남긴다(원본은 그대로 둠). 같은 내용의 보존본이 이미 있으면 새로 만들지 않는다.
    ///     실패해도 예외를 던지지 않는다.
    /// </summary>
    public static string? PreserveCorrupt(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) return null;

            var original = File.ReadAllBytes(fullPath);
            var directory = Path.GetDirectoryName(fullPath)!;
            foreach (var existing in Directory.EnumerateFiles(directory, Path.GetFileName(fullPath) + ".corrupt-*"))
                if (new FileInfo(existing).Length == original.Length
                    && File.ReadAllBytes(existing).AsSpan().SequenceEqual(original))
                    return existing;

            var copyPath = $"{fullPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(fullPath, copyPath, false);
            return copyPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            File.Delete(tempPath); // 이 호출이 만든 임시 파일만 정리(원본·백업은 건드리지 않음)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}