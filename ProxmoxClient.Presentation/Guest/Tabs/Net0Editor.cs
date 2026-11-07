using ProxmoxClient.Core.Models;

namespace ProxmoxClient.Presentation.Guest.Tabs;

/// <summary>간편 화면(하드웨어 탭)의 네트워크 칸 값.</summary>
internal sealed record NicInput(string Model, string Mac, string Bridge, string Vlan, bool Firewall, string Ip);

/// <summary>간편 화면의 net0 — 두 앱의 하드웨어 탭이 같이 쓴다.</summary>
internal static class Net0Editor
{
    /// <summary>
    ///     기존 net0 에 화면 칸만 반영한다 — 이 화면에 없는 설정(mtu·rate·queues·link_down·CT 의 gw·ip6 등)은
    ///     원래 값 그대로 둔다.
    /// </summary>
    public static string MergeNet0(string currentNet0, bool isVm, NicInput input)
    {
        var current = PropertyString.Parse(currentNet0);
        PropertyString updated;
        if (isVm)
        {
            // VM 형식은 첫 항목이 "모델=MAC" — 모델이 바뀔 수 있으므로 첫 항목을 새로 만들고 나머지를 뒤에 붙인다
            var oldModel = current.Items.Count > 0 ? current.Items[0].Key : null;
            var rest = current.Items.Where(kv => kv.Key != oldModel)
                .Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value));
            var head = input.Mac.Length > 0 ? $"{input.Model}={input.Mac}" : input.Model;
            updated = PropertyString.Parse(head).With(rest);
        }
        else
        {
            // 빈 IP 는 ip 키를 뺀다(예전처럼 dhcp 로 바꾸지 않는다)
            updated = (current.Has("name") ? current : current.With("name", "eth0")).With("ip", input.Ip);
        }

        return updated
            .With("bridge", input.Bridge)
            .With("tag", input.Vlan)
            .With("firewall", input.Firewall ? "1" : null)
            .Format();
    }
}
