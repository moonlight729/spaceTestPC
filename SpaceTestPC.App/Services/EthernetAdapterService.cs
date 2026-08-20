using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed record EthernetAdapterInfo(
    string Id,
    string Name,
    string Description,
    IPAddress Address,
    IPAddress Mask)
{
    public string DisplayText => $"{Name} - {Address}";
    public string Cidr => $"{GetNetworkAddress(Address, Mask)}/{GetPrefixLength(Mask)}";

    public bool Contains(IPAddress address)
    {
        var mask = ToUInt32(Mask);
        return (ToUInt32(Address) & mask) == (ToUInt32(address) & mask);
    }

    private static int GetPrefixLength(IPAddress mask) =>
        mask.GetAddressBytes().Sum(value => System.Numerics.BitOperations.PopCount(value));

    private static IPAddress GetNetworkAddress(IPAddress address, IPAddress mask) =>
        FromUInt32(ToUInt32(address) & ToUInt32(mask));

    private static uint ToUInt32(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    private static IPAddress FromUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}

public static class EthernetAdapterService
{
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "loopback", "tunnel", "tap", "vpn", "bluetooth"
    ];

    public static IReadOnlyList<EthernetAdapterInfo> GetAvailableAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsPhysicalEthernet)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork && address.IPv4Mask is not null)
                .Select(address => new EthernetAdapterInfo(
                    networkInterface.Id,
                    networkInterface.Name,
                    networkInterface.Description,
                    address.Address,
                    address.IPv4Mask!)))
            .OrderBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public static EthernetAdapterInfo Resolve(PcbaConnectionConfiguration configuration)
    {
        var adapters = GetAvailableAdapters();
        var selected = adapters.FirstOrDefault(adapter =>
                !string.IsNullOrWhiteSpace(configuration.AdapterId) &&
                string.Equals(adapter.Id, configuration.AdapterId, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault(adapter =>
                !string.IsNullOrWhiteSpace(configuration.LocalIp) &&
                string.Equals(adapter.Address.ToString(), configuration.LocalIp, StringComparison.OrdinalIgnoreCase));

        if (selected is not null)
        {
            return selected;
        }

        if (adapters.Count == 1)
        {
            return adapters[0];
        }

        if (adapters.Count == 0)
        {
            throw new InvalidOperationException("未找到已连接且具有 IPv4 地址的物理以太网卡。请检查网线和网卡状态。");
        }

        throw new InvalidOperationException("检测到多张可用物理以太网卡，请在设置中指定设备通信网卡。");
    }

    private static bool IsPhysicalEthernet(NetworkInterface networkInterface)
    {
        if (networkInterface.OperationalStatus != OperationalStatus.Up ||
            networkInterface.NetworkInterfaceType is not (
                NetworkInterfaceType.Ethernet or
                NetworkInterfaceType.GigabitEthernet or
                NetworkInterfaceType.FastEthernetFx or
                NetworkInterfaceType.FastEthernetT))
        {
            return false;
        }

        var identity = $"{networkInterface.Name} {networkInterface.Description}";
        return !VirtualMarkers.Any(marker => identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
