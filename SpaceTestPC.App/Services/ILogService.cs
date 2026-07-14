namespace SpaceTestPC.App.Services;

public interface ILogService
{
    void Info(string message);
    IReadOnlyList<string> Snapshot();
}
