namespace SpaceTestPC.App.Services;

public interface IStatusMonitorService
{
    Task StartAsync(string sessionId, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    string Status { get; }
}
