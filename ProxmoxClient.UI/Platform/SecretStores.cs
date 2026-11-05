namespace ProxmoxClient.UI.Platform;

/// <summary>운영체제별 비밀값 저장소 선택 — OS 분기는 이곳 한 군데에서만 한다.</summary>
public static class SecretStores
{
    /// <summary>
    ///     Windows: 자격 증명 관리자 · macOS: 키체인 · Linux: Secret Service(libsecret, secret-tool) ·
    ///     그 밖(또는 저장소를 쓸 수 없음): 메모리. Android 는 헤드 프로젝트가 키스토어 구현으로 바꿔 넣는다.
    /// </summary>
    public static ISecretStore CreateDefault()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialSecretStore();
        if (OperatingSystem.IsMacOS()) return new MacKeychainSecretStore();
        if (OperatingSystem.IsLinux() && LinuxSecretToolStore.IsAvailable()) return new LinuxSecretToolStore();
        return new MemorySecretStore();
    }

    /// <summary>OS 저장소에 쓰는 항목 이름 — 다른 프로그램의 항목과 섞이지 않게 앞에 붙인다.</summary>
    internal const string Service = "ProxmoxClient";
}
