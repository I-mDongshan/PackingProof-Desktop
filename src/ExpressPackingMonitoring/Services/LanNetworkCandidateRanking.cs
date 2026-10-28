using System.Net;
using System.Net.NetworkInformation;

namespace ExpressPackingMonitoring.Services;

internal sealed record LanNetworkCandidate(
    IPAddress Address,
    IPAddress? IPv4Mask,
    string InterfaceName,
    string InterfaceDescription,
    NetworkInterfaceType InterfaceType,
    bool HasGateway,
    bool HasDnsServer,
    bool IsLikelyVirtual,
    int Score);

/// <summary>
/// 为多网卡电脑选择更适合作为局域网访问地址的 IPv4 地址。
/// 虚拟网卡仍保留为候选，只降低排序，避免运行在虚拟机或纯 VPN 环境时完全失去地址。
/// </summary>
internal static class LanNetworkCandidateRanking
{
    private static readonly string[] VirtualAdapterMarkers =
    [
        "hyper-v virtual",
        "virtual ethernet",
        "virtual adapter",
        "virtual switch",
        "vmware",
        "virtualbox",
        "vethernet",
        "vswitch",
        "vmnet",
        "tap-windows",
        "wintun",
        "wireguard",
        "tailscale",
        "zerotier",
        "openvpn",
        "hamachi",
        "radmin vpn",
        "docker",
        "wsl",
        "wi-fi direct",
        "wan miniport",
        "multiplexor",
        "kernel debug",
        "bluetooth",
        "pseudo-interface",
        "npcap loopback"
    ];

    internal static LanNetworkCandidate Create(
        IPAddress address,
        IPAddress? ipv4Mask,
        string interfaceName,
        string interfaceDescription,
        NetworkInterfaceType interfaceType,
        bool hasGateway,
        bool hasDnsServer)
    {
        ArgumentNullException.ThrowIfNull(address);
        string combinedName = $"{interfaceName} {interfaceDescription}";
        bool isVirtual = IsLikelyVirtualAdapter(combinedName);

        int score = 0;
        if (hasGateway) score += 80;
        if (IsPrivateLanAddress(address)) score += 60;
        if (interfaceType is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.Wireless80211)
        {
            score += 30;
        }
        if (hasDnsServer) score += 10;
        if (isVirtual) score -= 240;

        return new LanNetworkCandidate(
            address,
            ipv4Mask,
            interfaceName,
            interfaceDescription,
            interfaceType,
            hasGateway,
            hasDnsServer,
            isVirtual,
            score);
    }

    internal static IReadOnlyList<LanNetworkCandidate> Rank(
        IEnumerable<LanNetworkCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Address, IpAddressComparer.Instance)
            .ToArray();
    }

    internal static bool IsLikelyVirtualAdapter(string nameAndDescription)
    {
        if (string.IsNullOrWhiteSpace(nameAndDescription))
            return false;

        return VirtualAdapterMarkers.Any(marker =>
            nameAndDescription.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsUsableLanAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return false;

        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return false;

        // 0.0.0.0、APIPA、组播、广播和 RFC 2544 基准地址段不能作为局域网访问地址。
        if (bytes[0] == 0)
            return false;
        if (bytes[0] == 169 && bytes[1] == 254)
            return false;
        if (bytes[0] >= 224)
            return false;
        if (bytes[0] == 255)
            return false;
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            return false;

        return true;
    }

    private static bool IsPrivateLanAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return false;

        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private sealed class IpAddressComparer : IComparer<IPAddress>
    {
        internal static readonly IpAddressComparer Instance = new();

        public int Compare(IPAddress? x, IPAddress? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x == null)
                return -1;
            if (y == null)
                return 1;

            byte[] left = x.GetAddressBytes();
            byte[] right = y.GetAddressBytes();
            int length = Math.Min(left.Length, right.Length);
            for (int i = 0; i < length; i++)
            {
                int comparison = left[i].CompareTo(right[i]);
                if (comparison != 0)
                    return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
