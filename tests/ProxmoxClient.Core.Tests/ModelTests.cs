using ProxmoxClient.Core.Api;
using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Core.Tests;

/// <summary>
///     PropertyString 기본 동작 — 따옴표·이스케이프 규칙은 바뀔 수 있어 여기서는 다루지 않고,
///     단순한 key=value 를 쉼표로 잇는 모양만 확인한다.
/// </summary>
public class PropertyStringTests
{
    [Fact]
    public void ParsesKeyValuePairsInOrder()
    {
        var p = PropertyString.Parse("virtio=BC:24:11:AA:BB:CC,bridge=vmbr0,firewall=1");
        Assert.Equal(["virtio", "bridge", "firewall"], p.Items.Select(kv => kv.Key));
        Assert.Equal("BC:24:11:AA:BB:CC", p["virtio"]);
        Assert.Equal("vmbr0", p.Get("bridge"));
        Assert.True(p.IsOn("firewall"));
        Assert.False(p.Has("tag"));
        Assert.Null(p["tag"]);
        Assert.Equal("x", p.Get("tag", "x"));
    }

    [Fact]
    public void DefaultKeyTakesBareItem()
    {
        var p = PropertyString.Parse("local-lvm:vm-100-disk-0,iothread=1,size=32G", "file");
        Assert.Equal("local-lvm:vm-100-disk-0", p["file"]);
        Assert.Equal("32G", p["size"]);
        Assert.Equal("local-lvm:vm-100-disk-0,iothread=1,size=32G", p.Format("file"));
    }

    [Fact]
    public void EmptyInputIsEmpty()
    {
        Assert.Empty(PropertyString.Parse(null).Items);
        Assert.Empty(PropertyString.Parse("  ").Items);
        Assert.Equal("", PropertyString.Parse("").Format());
    }

    [Fact]
    public void FormatRoundTripsSimpleValues()
    {
        const string text = "order=1,up=30,down=60";
        Assert.Equal(text, PropertyString.Parse(text).Format());
    }

    [Fact]
    public void WithReplacesInPlaceAppendsAndRemoves()
    {
        var p = PropertyString.Parse("a=1,b=2,c=3");
        Assert.Equal("a=1,b=9,c=3", p.With("b", "9").Format());
        Assert.Equal("a=1,b=2,c=3,d=4", p.With("d", "4").Format());
        Assert.Equal("a=1,c=3", p.With("b", null).Format());
        Assert.Equal("a=1,b=2,c=3", p.Format()); // 원본은 그대로
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("yes", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    public void IsOnRecognizesTruthyValues(string value, bool expected)
    {
        Assert.Equal(expected, PropertyString.Parse($"x={value}").IsOn("x"));
    }
}

public class UpidTests
{
    [Fact]
    public void ParsesAllFields()
    {
        // 시작 시각은 16진수 유닉스 시간
        var task = ProxmoxApiClient.ParseUpid("UPID:pve1:0000A1B2:0012C3D4:68E5A6C0:qmstart:100:root@pam:");
        Assert.Equal("pve1", task.Node);
        Assert.Equal("qmstart", task.Type);
        Assert.Equal("100", task.Id);
        Assert.Equal("root@pam", task.User);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(0x68E5A6C0).UtcDateTime, task.StartTimeUtc);
        Assert.Equal("UPID:pve1:0000A1B2:0012C3D4:68E5A6C0:qmstart:100:root@pam:", task.Upid);
    }

    [Fact]
    public void EmptyIdAndTokenUser()
    {
        var task = ProxmoxApiClient.ParseUpid("UPID:pve2:00001234:00005678:68E5A6C0:aptupdate::root@pam!ci:");
        Assert.Equal("aptupdate", task.Type);
        Assert.Equal("", task.Id);
        Assert.Equal("root@pam!ci", task.User);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-upid")]
    [InlineData("UPID:pve1:1:2")]
    [InlineData("TASK:pve1:1:2:3:t:i:u:")]
    public void MalformedGivesEmptyTask(string text)
    {
        var task = ProxmoxApiClient.ParseUpid(text);
        Assert.Equal(text, task.Upid);
        Assert.Equal("", task.Node);
        Assert.Equal("", task.Type);
    }

    [Fact]
    public void BadStartTimeLeavesDefault()
    {
        var task = ProxmoxApiClient.ParseUpid("UPID:pve1:1:2:zzzz:qmstart:100:root@pam:");
        Assert.Equal("pve1", task.Node);
        Assert.Equal(default, task.StartTimeUtc);
    }
}
