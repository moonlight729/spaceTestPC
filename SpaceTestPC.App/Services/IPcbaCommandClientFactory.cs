using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public interface IPcbaCommandClientFactory
{
    IPcbaCommandClient Create(PcbaConnectionMode mode);
}
