using SpaceTestPC.Core.Models;

namespace SpaceTestPC.Core.Services;

public interface IPcbaCommandClientFactory
{
    IPcbaCommandClient Create(PcbaConnectionMode mode);
}
