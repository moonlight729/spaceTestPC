using SpaceTestPC.Core.Models;

namespace SpaceTestPC.Core.Services;

public interface IDatabaseRepository
{
    Task SaveSessionAsync(TestSessionRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TestSessionRecord>> GetRecentSessionsAsync(int count, CancellationToken cancellationToken = default);
    Task<TestSessionRecord?> GetLatestSessionBySnAsync(string sn, CancellationToken cancellationToken = default);
}
