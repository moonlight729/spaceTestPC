using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class PcbaCommandClientFactory : IPcbaCommandClientFactory
{
    private readonly IPcbaCommandClient _mockClient;
    private readonly IPcbaCommandClient _adbClient;

    public PcbaCommandClientFactory(IPcbaCommandClient mockClient, IPcbaCommandClient adbClient)
    {
        _mockClient = mockClient;
        _adbClient = adbClient;
    }

    public IPcbaCommandClient Create(PcbaConnectionMode mode)
    {
        return mode switch
        {
            PcbaConnectionMode.AdbForward => _adbClient,
            _ => _mockClient
        };
    }
}
