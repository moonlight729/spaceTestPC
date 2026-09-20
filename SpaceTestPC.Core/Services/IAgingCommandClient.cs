using SpaceTestPC.Core.Models;

namespace SpaceTestPC.Core.Services;

/// <summary>
/// 老化命令组客户端（§5.1）。每个槽位持有一个实例，实例之间不共享连接状态，
/// 因此可以 32 路并行。
/// </summary>
public interface IAgingCommandClient
{
    string Host { get; }

    Task<AgingStartAck> StartAsync(AgingStartRequest request, CancellationToken cancellationToken = default);

    Task<bool> StopAsync(CancellationToken cancellationToken = default);

    Task<AgingStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>设备尚未产出结论时返回 null。</summary>
    Task<AgingRunResult?> GetResultAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 清理 media/（§8.9）。scope=media 只删大数据，scope=all 连元数据一起删。
    /// 删除耗时可能达数分钟，因此超时单独指定。
    /// </summary>
    Task<AgingCleanupInfo> CleanupAsync(string scope = "media", int timeoutMs = 600000, CancellationToken cancellationToken = default);
}
