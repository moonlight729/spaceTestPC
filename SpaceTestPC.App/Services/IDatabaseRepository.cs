using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public interface IDatabaseRepository
{
    Task SaveSessionAsync(TestSessionRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TestSessionRecord>> GetRecentSessionsAsync(int count, CancellationToken cancellationToken = default);
}
