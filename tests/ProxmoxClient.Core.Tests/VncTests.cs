using ProxmoxClient.Core.Vnc;

namespace ProxmoxClient.Core.Tests;

/// <summary>VNC 인증(DES)·픽셀 변환·변경 영역 — 작은 순수 함수 검사.</summary>
public class VncDesAuthTests
{
    // 기대값은 openssl enc -des-ecb -nopad 로 따로 계산했다(키 = 비밀번호 각 바이트를 비트 반전)
    [Theory]
    // FIPS 81 예제(키 0123456789ABCDEF, "Now is the time ") — 비밀번호는 그 키를 비트 반전한 글자(패리티 비트는 무시됨)
    [InlineData("\0D\"f\u0011U3w", "4e6f77206973207468652074696d6520", "3fa40e8a984d48156a271787ab8883f9")]
    [InlineData("password", "000102030405060708090a0b0c0d0e0f", "b866924125c8eebb9debc1db61c538e2")]
    // 8자보다 짧으면 0 으로 채운다
    [InlineData("pass", "000102030405060708090a0b0c0d0e0f", "5fb02f4e6ec9fda06c41df1f35015138")]
    public void KnownAnswer(string password, string challengeHex, string expectedHex)
    {
        var response = VncDesAuth.CreateResponse(Convert.FromHexString(challengeHex), password);
        Assert.Equal(expectedHex, Convert.ToHexStringLower(response));
    }

    [Fact]
    public void OnlyFirstEightCharactersCount()
    {
        var challenge = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        Assert.Equal(VncDesAuth.CreateResponse(challenge, "password"),
            VncDesAuth.CreateResponse(challenge, "password-and-more"));
    }

    [Fact]
    public void RejectsWrongChallengeLength()
    {
        Assert.Throws<ArgumentException>(() => VncDesAuth.CreateResponse(new byte[8], "x"));
        Assert.Throws<ArgumentNullException>(() => VncDesAuth.CreateResponse(null!, "x"));
    }
}

public class PixelConvertTests
{
    [Fact]
    public void ToBgrxPacksRgbWithOpaqueAlpha()
    {
        Assert.Equal(0xFF123456u, PixelConvert.ToBgrx(0x12, 0x34, 0x56));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(17)] // 벡터 경로(4픽셀씩)와 나머지 스칼라 경로를 모두 지난다
    [InlineData(64)]
    public void RgbToBgrxMatchesScalar(int count)
    {
        var rgb = new byte[count * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 37 + 11);
        var dst = new uint[count];

        PixelConvert.RgbToBgrx(rgb, dst);

        for (var px = 0; px < count; px++)
            Assert.Equal(PixelConvert.ToBgrx(rgb[px * 3], rgb[px * 3 + 1], rgb[px * 3 + 2]), dst[px]);
    }

    [Fact]
    public void Expand1BitReadsMostSignificantBitFirst()
    {
        const uint a = 0xFF000000, b = 0xFFFFFFFF;
        var dst = new uint[11]; // 한 바이트(8) + 남은 3픽셀
        PixelConvert.Expand1Bit([0b1010_0001, 0b0110_0000], dst, a, b);
        Assert.Equal(new[] { b, a, b, a, a, a, a, b, a, b, b }, dst);
    }
}

public class DirtyRegionTests
{
    private static DirtyRegion.Rect[] Rects(DirtyRegion region)
    {
        var rects = new DirtyRegion.Rect[DirtyRegion.Capacity];
        return rects[..region.CopyTo(rects)];
    }

    [Fact]
    public void IgnoresEmptyAndContainedRects()
    {
        var region = new DirtyRegion();
        region.Add(0, 0, 0, 10);
        region.Add(0, 0, 10, -1);
        Assert.Equal(0, region.Count);

        region.Add(0, 0, 100, 100);
        region.Add(10, 10, 5, 5);
        Assert.Equal([new DirtyRegion.Rect(0, 0, 100, 100)], Rects(region));
    }

    [Fact]
    public void MergesAdjacentRects()
    {
        var region = new DirtyRegion();
        region.Add(0, 0, 10, 10);
        region.Add(10, 0, 10, 10); // 맞닿음 — 합쳐도 면적이 늘지 않는다
        Assert.Equal([new DirtyRegion.Rect(0, 0, 20, 10)], Rects(region));
    }

    [Fact]
    public void KeepsDistantRectsSeparate()
    {
        var region = new DirtyRegion();
        region.Add(0, 0, 10, 10);
        region.Add(1000, 700, 20, 20);
        Assert.Equal(2, region.Count);
    }

    [Fact]
    public void NeverExceedsCapacityAndCoversEverything()
    {
        var region = new DirtyRegion();
        var added = new List<DirtyRegion.Rect>();
        for (var i = 0; i < 40; i++)
        {
            var x = i * 50 % 1900;
            var y = i * 97 % 1000;
            region.Add(x, y, 8, 8);
            added.Add(new DirtyRegion.Rect(x, y, x + 8, y + 8));
            Assert.InRange(region.Count, 1, DirtyRegion.Capacity);
        }

        var rects = Rects(region);
        foreach (var r in added) Assert.Contains(rects, c => c.Contains(r));
    }

    [Fact]
    public void ClearEmpties()
    {
        var region = new DirtyRegion();
        region.Add(1, 1, 1, 1);
        region.Clear();
        Assert.Equal(0, region.Count);
    }
}
