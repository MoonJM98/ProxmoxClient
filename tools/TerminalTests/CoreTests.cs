using System.Text.Json;
using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Profiles;
using ProxmoxClient.Core.Settings;

/// <summary>Core — API 오류 문구, 손상 파일 .bak 복구, 프록시 비밀번호 저장, 작업 대기 재시도.</summary>
public static class CoreTests
{
    public static void ApiErrorUsesReasonPhrase(Checker c)
    {
        var ex = ProxmoxApiException.FromResponse(500, "VM 100 already running", "{\"data\":null}");
        c.Equal(500, ex.StatusCode, "상태 코드");
        c.Equal("VM 100 already running", ex.ReasonPhrase, "사유 문구 보관");
        c.Is(ex.Message.Contains("VM 100 already running"), "메시지에 사유 문구", ex.Message);
        c.Is(!ex.Message.Contains("\"data\""), "빈 봉투 본문은 메시지에 넣지 않음", ex.Message);
        c.Equal(0, ex.FieldErrors.Count, "칸별 오류 없음");
    }

    public static void ApiErrorParsesFieldErrors(Checker c)
    {
        const string body = "{\"data\":null,\"errors\":{\"vmid\":\"invalid format - value '0' is out of range\\n\",\"name\":\"too long\"}}";
        var ex = ProxmoxApiException.FromResponse(400, "Parameter verification failed.", body);
        c.Equal(2, ex.FieldErrors.Count, "칸별 오류 수");
        c.Equal("invalid format - value '0' is out of range", ex.FieldErrors.GetValueOrDefault("vmid"), "vmid 오류(끝 줄바꿈 제거)");
        c.Is(ex.Message.Contains("Parameter verification failed."), "메시지에 사유", ex.Message);
        c.Is(ex.Message.Contains("vmid: invalid format"), "메시지에 칸 오류", ex.Message);
        c.Is(ex.Message.Contains("name: too long"), "메시지에 두 번째 칸 오류", ex.Message);
        c.Is(!ex.Message.Contains("{"), "본문 JSON 은 그대로 넣지 않음", ex.Message);
    }

    public static void ApiErrorFallsBackToBody(Checker c)
    {
        var ex = ProxmoxApiException.FromResponse(502, null, "Bad Gateway from upstream");
        c.Is(ex.Message.Contains("Bad Gateway from upstream"), "사유가 없으면 본문 발췌", ex.Message);
        var empty = ProxmoxApiException.FromResponse(500, "", null);
        c.Is(empty.Message.Contains("500"), "본문·사유 없어도 상태 코드", empty.Message);
    }

    public static void ProfileRestoredFromBackup(Checker c)
    {
        var dir = TempDir();
        try
        {
            var store = new ProfileStore(dir);
            var profile = new ConnectionProfile { Name = "first-save", Host = "pve1" };
            store.SaveAsync(profile).GetAwaiter().GetResult();
            profile.Name = "second-save";
            store.SaveAsync(profile).GetAwaiter().GetResult(); // 첫 저장본이 .bak 으로 남는다
            var path = Path.Combine(dir, $"{profile.Id}.json");
            c.Is(File.Exists(path + ".bak"), ".bak 생성");

            File.WriteAllText(path, "{ 손상된 내용");
            var loaded = store.LoadAsync().GetAwaiter().GetResult();
            c.Equal(1, loaded.Count, "백업으로 되살린 프로필 수");
            c.Equal("first-save", loaded.FirstOrDefault()?.Name, "백업 내용");
            c.Is(File.ReadAllText(path).Contains("first-save"), "원본 자리에 백업 복원");
            c.Is(Directory.EnumerateFiles(dir, "*.corrupt-*").Any(), "손상본 사본 보존");

            File.WriteAllText(path, "{ 손상");
            File.WriteAllText(path + ".bak", "역시 손상");
            var none = store.LoadAsync().GetAwaiter().GetResult();
            c.Equal(0, none.Count, "둘 다 손상이면 건너뜀(예외 없음)");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    public static void SettingsRestoredFromBackup(Checker c)
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "app-settings.json");
            var store = new AppSettingsStore(path);
            store.SaveAsync(new AppSettings { RefreshIntervalSeconds = 7 }).GetAwaiter().GetResult();
            store.SaveAsync(new AppSettings { RefreshIntervalSeconds = 9 }).GetAwaiter().GetResult();
            File.WriteAllText(path, "{ broken");

            var loaded = store.LoadAsync().GetAwaiter().GetResult();
            c.Equal(7, loaded.RefreshIntervalSeconds, "설정 .bak 복원");
            c.Is(File.ReadAllText(path).Contains("7"), "설정 원본 자리에 복원");

            File.WriteAllText(path, "{ broken");
            File.Delete(path + ".bak");
            c.Equal(AppSettings.DefaultRefreshIntervalSeconds, store.LoadAsync().GetAwaiter().GetResult()
                .RefreshIntervalSeconds, "백업이 없으면 기본값");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    public static void ProxyPasswordNotSerializedOffWindows(Checker c)
    {
        if (OperatingSystem.IsWindows())
        {
            c.Is(ConnectionProfile.ProxyPasswordInProfile, "Windows 는 프로필에 암호화 저장");
            return;
        }

        c.Is(!ConnectionProfile.ProxyPasswordInProfile, "Windows 가 아니면 프로필에 저장 안 함");
        var profile = new ConnectionProfile { Host = "pve", ProxyMode = ProxyMode.Http, ProxyPassword = "s3cret-proxy" };
        string json = "";
        try
        {
            json = JsonSerializer.Serialize(profile);
        }
        catch (Exception ex)
        {
            c.Fail("직렬화 예외 " + ex.GetType().Name);
        }

        c.Is(!json.Contains("s3cret-proxy"), "평문 없음", json);
        c.Is(!json.Contains("ProxyPasswordProtected"), "암호값 칸 없음", json);

        // 예전 평문 저장 프로필 — 불러오기·이전이 실패하지 않고 파일에서 평문이 사라진다
        var dir = TempDir();
        try
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(dir, $"{id}.json");
            File.WriteAllText(path, $"{{\"Id\":\"{id}\",\"Name\":\"legacy\",\"Host\":\"pve\",\"ProxyPassword\":\"old-plain\"}}");
            var loaded = new ProfileStore(dir).LoadAsync().GetAwaiter().GetResult();
            c.Equal(1, loaded.Count, "레거시 프로필 표시");
            c.Equal("old-plain", loaded.FirstOrDefault()?.ProxyPassword, "메모리에는 읽어 온 값(앱이 보안 저장소로 옮김)");
            c.Is(!File.ReadAllText(path).Contains("old-plain"), "파일의 평문 제거");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    public static void ProxyHostIpv6AndBadAddress(Checker c)
    {
        try
        {
            using var client = new ProxmoxApiClient(new ConnectionProfile
            {
                Host = "fe80::1", ProxyMode = ProxyMode.Http, ProxyHost = "fd00::5", ProxyPort = 3128
            });
            c.Is(true, "IPv6 프록시 주소 허용");
        }
        catch (Exception ex)
        {
            c.Fail("IPv6 프록시 예외 " + ex.Message);
        }

        try
        {
            using var client = new ProxmoxApiClient(new ConnectionProfile { Host = "bad host/x", Port = 8006 });
            c.Fail("잘못된 주소가 통과");
        }
        catch (ProxmoxApiException ex)
        {
            c.Is(ex.StatusCode == 0, "잘못된 주소는 API 오류", ex.Message);
        }
        catch (Exception ex)
        {
            c.Fail("잘못된 주소가 다른 예외 " + ex.GetType().Name);
        }
    }

    public static void PollRetryPolicyRules(Checker c)
    {
        c.Is(PollRetryPolicy.IsTransient(new ProxmoxApiException(0, null, "timeout")), "상태 0 재시도");
        c.Is(PollRetryPolicy.IsTransient(new ProxmoxApiException(503, null)), "5xx 재시도");
        c.Is(PollRetryPolicy.IsTransient(new HttpRequestException("x")), "전송 오류 재시도");
        c.Is(!PollRetryPolicy.IsTransient(new ProxmoxApiException(401, null)), "401 끝");
        c.Is(!PollRetryPolicy.IsTransient(new ProxmoxApiException(403, null)), "403 끝");
        c.Is(!PollRetryPolicy.IsTransient(new OperationCanceledException()), "취소 끝");

        var policy = new PollRetryPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(1), MaxDelay = TimeSpan.FromMilliseconds(4),
            GiveUpAfter = TimeSpan.FromMilliseconds(300)
        };
        c.Equal(TimeSpan.FromMilliseconds(2), policy.DelayFor(2), "두 배 간격");
        c.Equal(TimeSpan.FromMilliseconds(4), policy.DelayFor(10), "간격 상한");

        // 일시 오류 두 번 → 실행 중 → 완료
        var calls = 0;
        var result = policy.PollAsync(_ =>
        {
            calls++;
            return calls switch
            {
                1 => throw new ProxmoxApiException(0, null, "끊김"),
                2 => throw new ProxmoxApiException(502, null),
                3 => Task.FromResult("running"),
                _ => Task.FromResult("OK")
            };
        }, s => s != "running", TimeSpan.FromMilliseconds(1), CancellationToken.None).GetAwaiter().GetResult();
        c.Equal("OK", result, "일시 오류 뒤 완료");
        c.Equal(4, calls, "호출 수");

        // 4xx 는 바로 끝
        calls = 0;
        var forbidden = Catch(() => policy.PollAsync<string>(_ =>
        {
            calls++;
            throw new ProxmoxApiException(403, null);
        }, _ => true, TimeSpan.FromMilliseconds(1), CancellationToken.None));
        c.Is(forbidden is ProxmoxApiException { StatusCode: 403 }, "403 은 그대로", forbidden?.GetType().Name);
        c.Equal(1, calls, "403 은 한 번만");

        // 계속 실패하면 제한 시간 뒤 마지막 오류
        calls = 0;
        var down = Catch(() => policy.PollAsync<string>(_ =>
        {
            calls++;
            throw new ProxmoxApiException(0, null, "응답 없음");
        }, _ => true, TimeSpan.FromMilliseconds(1), CancellationToken.None));
        c.Is(down is ProxmoxApiException { StatusCode: 0 }, "포기 후 마지막 오류", down?.GetType().Name);
        c.Is(calls > 2, "포기 전 여러 번 시도", calls);

        // 취소
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = Catch(() => policy.PollAsync(_ => Task.FromResult("running"), _ => false,
            TimeSpan.FromMilliseconds(1), cts.Token));
        c.Is(cancelled is OperationCanceledException, "취소는 바로 끝", cancelled?.GetType().Name);
    }

    private static Exception? Catch(Func<Task> action)
    {
        try
        {
            action().GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pxc-coretests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
