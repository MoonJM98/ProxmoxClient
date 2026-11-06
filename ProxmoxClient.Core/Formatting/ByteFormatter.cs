using System.Globalization;
using ProxmoxClient.Core.Settings;

namespace ProxmoxClient.Core.Formatting;

/// <summary>
///     바이트 수 표시 형식 — 두 앱(WPF·Avalonia)이 함께 쓴다.
///     <see cref="Format" />: 목록·상세·그래프 축·툴팁. 숫자는 "#,##0.##"(천 단위 구분, 소수 최대 2자리), 단위는 설정(<see cref="DisplayUnit" />)에 따라 자동 또는 고정.
///     <see cref="FormatIec" />: 하드웨어 목록 — 웹 UI 의 format_size 와 같은 모양.
/// </summary>
public static class ByteFormatter
{
    private const string NumberFormat = "#,##0.##";
    private const double BytesPerStep = 1024;

    /// <summary>인덱스 = 1024 거듭제곱 지수(ByteDisplayUnit 값과 일치: KB=1, MB=2 ...).</summary>
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    private static readonly string[] IecUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>앱 설정의 표시 단위 — UI 스레드에서 시작·설정 저장 시 갱신한다.</summary>
    public static ByteDisplayUnit DisplayUnit { get; set; } = ByteDisplayUnit.Auto;

    public static string Format(double bytes)
    {
        var exponent = DisplayUnit == ByteDisplayUnit.Auto ? AutoExponent(bytes) : (int)DisplayUnit;
        exponent = Math.Clamp(exponent, 0, Units.Length - 1);
        var scaled = bytes / Math.Pow(BytesPerStep, exponent);
        return $"{scaled.ToString(NumberFormat, CultureInfo.CurrentCulture)} {Units[exponent]}";
    }

    /// <summary>웹 UI 의 format_size — IEC 단위, 소수 둘째 자리("32.00 GiB"). 바이트는 정수. 표시 단위 설정과 무관.</summary>
    public static string FormatIec(double bytes)
    {
        var unit = 0;
        while (bytes >= BytesPerStep && unit < IecUnits.Length - 1)
        {
            bytes /= BytesPerStep;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString("0", CultureInfo.InvariantCulture)} B"
            : $"{bytes.ToString("0.00", CultureInfo.InvariantCulture)} {IecUnits[unit]}";
    }

    /// <summary>1 이상이 되는 가장 큰 단위.</summary>
    private static int AutoExponent(double bytes)
    {
        var exponent = 0;
        var size = Math.Abs(bytes);
        while (size >= BytesPerStep && exponent < Units.Length - 1)
        {
            size /= BytesPerStep;
            exponent++;
        }

        return exponent;
    }
}
