using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;

namespace ProxmoxClient.UI.Platform;

/// <summary>
///     지금 화면을 그리는 GPU 방식 — 합성기가 주고받을 수 있는 GPU 자원 종류로 판별한다(Vulkan · Direct3D 11(ANGLE) ·
///     Metal · OpenGL/EGL, 없으면 소프트웨어). 각 OS 의 렌더링 설정(헤드의 RenderingMode 우선순위)이 실제로 어느 방식으로
///     떨어졌는지 기기에서 바로 확인하려고 첫 화면에 작게 보인다.
/// </summary>
public static class RenderInfo
{
    public static async Task<string> DescribeAsync(Visual visual)
    {
        // 1) Avalonia 가 만든 그래픽 백엔드 객체 이름(가장 정확) — 비공개 형식이라 리플렉션, 못 읽으면 2) 로
        if (BackendName() is { } backend) return backend;

        if (ElementComposition.GetElementVisual(visual)?.Compositor is not { } compositor) return "?";

        ICompositionGpuInterop? interop;
        try
        {
            interop = await compositor.TryGetCompositionGpuInterop();
        }
        catch (Exception)
        {
            interop = null;
        }

        if (interop is null) return "Software";

        var types = interop.SupportedImageHandleTypes;
        if (types.Any(t => t.StartsWith("Vulkan", StringComparison.Ordinal))) return "Vulkan";
        if (types.Any(t => t.StartsWith("D3D11", StringComparison.Ordinal))) return "Direct3D 11 (ANGLE)";
        if (types.Contains(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef)) return "Metal";
        return types.Count > 0 ? "OpenGL (" + string.Join(", ", types) + ")" : "GPU";
    }

    private static string? BackendName()
    {
        try
        {
            var assembly = typeof(AvaloniaObject).Assembly;
            var locator = assembly.GetType("Avalonia.AvaloniaLocator");
            var graphicsType = assembly.GetType("Avalonia.Platform.IPlatformGraphics");
            var current = locator?.GetProperty("Current",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static)?.GetValue(null);
            if (current is null || graphicsType is null) return null;

            var getService = current.GetType().GetMethod("GetService", [typeof(Type)]);
            var graphics = getService?.Invoke(current, [graphicsType]);
            if (Environment.GetEnvironmentVariable("PROXMOXCLIENT_LOG") == "1")
                Console.Error.WriteLine($"[render] locator={current.GetType().FullName} graphics={graphics?.GetType().FullName ?? "null"}");
            if (graphics is null) return "Software";

            var name = graphics.GetType().Name;
            if (name.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)) return "Vulkan";
            if (name.Contains("Angle", StringComparison.OrdinalIgnoreCase)) return "Direct3D 11 (ANGLE)";
            if (name.Contains("Metal", StringComparison.OrdinalIgnoreCase)) return "Metal";
            if (name.Contains("Glx", StringComparison.OrdinalIgnoreCase)) return "OpenGL (GLX)";
            if (name.Contains("Egl", StringComparison.OrdinalIgnoreCase)) return "OpenGL ES (EGL)";
            return name;
        }
        catch (Exception)
        {
            return null; // 형식이 바뀌었거나(버전) 잘려 나감(AOT) — GPU 상호 운용으로 판별
        }
    }

    /// <summary>"Vulkan · 1.0.3 · Android" 형식 — 첫 화면 아래 작은 글씨.</summary>
    public static async Task<string> SummaryAsync(Visual visual)
    {
        var version = typeof(RenderInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
        var os = OperatingSystem.IsAndroid() ? "Android" : OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "?";
        return $"{await DescribeAsync(visual)} · {os} · {PlatformInfo.FormFactor}" + (version.Length > 0 ? $" · {version}" : "");
    }
}

/// <summary>렌더링 방식을 보여 주는 작은 글자(첫 화면 아래).</summary>
public sealed class RenderInfoText : TextBlock
{
    public RenderInfoText()
    {
        Classes.Add("subtle");
        FontSize = 11;
        Opacity = 0.7;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        Loaded += async (_, _) => Text = await RenderInfo.SummaryAsync(this);
    }
}
