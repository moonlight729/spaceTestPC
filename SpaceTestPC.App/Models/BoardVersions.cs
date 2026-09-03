namespace SpaceTestPC.App.Models;
public sealed class BoardVersions
{
    public string UbootVersion { get; init; } = "--";
    public string KernelVersion { get; init; } = "--";
    public string RootfsVersion { get; init; } = "--";
}
public sealed class BoardVersionsEnvelope { public BoardVersions Data { get; init; } = new(); }
