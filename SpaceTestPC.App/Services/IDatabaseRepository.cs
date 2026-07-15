using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public interface IDatabaseRepository
{
    Task SaveSessionAsync(TestSessionRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TestSessionRecord>> GetRecentSessionsAsync(int count, CancellationToken cancellationToken = default);
    Task<TestSessionRecord?> GetLatestSessionBySnAsync(string sn, CancellationToken cancellationToken = default);
}
