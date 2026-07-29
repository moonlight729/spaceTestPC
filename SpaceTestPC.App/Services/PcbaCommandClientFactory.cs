using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class PcbaCommandClientFactory : IPcbaCommandClientFactory
{
    private readonly IPcbaCommandClient _mockClient;
    private readonly IPcbaCommandClient _adbClient;
    private readonly IPcbaCommandClient _tcpClient;

    public PcbaCommandClientFactory(IPcbaCommandClient mockClient, IPcbaCommandClient adbClient, IPcbaCommandClient tcpClient)
    {
        _mockClient = mockClient;
        _adbClient = adbClient;
        _tcpClient = tcpClient;
    }

    public IPcbaCommandClient Create(PcbaConnectionMode mode)
    {
        return mode switch
        {
            PcbaConnectionMode.AdbForward => _adbClient,
            PcbaConnectionMode.Tcp => _tcpClient,
            _ => _mockClient
        };
    }
}
