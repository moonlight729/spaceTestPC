namespace SpaceTestPC.App.Services;

public interface ILogService
{
    void Info(string message);
    void Event(string eventName, object payload);
    IReadOnlyList<string> Snapshot();
}
