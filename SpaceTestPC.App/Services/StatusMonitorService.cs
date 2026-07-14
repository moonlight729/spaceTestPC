namespace SpaceTestPC.App.Services;

public sealed class StatusMonitorService : IStatusMonitorService
{
    private readonly string _name;

    public StatusMonitorService(string name)
    {
        _name = name;
    }

    public string Status { get; private set; } = "Idle";

    public Task StartAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        Status = $"Running for {sessionId[..8]}";
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Status = "Idle";
        return Task.CompletedTask;
    }
}
