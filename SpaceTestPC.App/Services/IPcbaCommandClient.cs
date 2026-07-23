using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public interface IPcbaCommandClient
{
    Task<ApplicationMd5Info> GetApplicationMd5Async(string remoteBinaryPath, CancellationToken cancellationToken = default);
    Task<ApplicationUpgradeResult> UpgradeApplicationAsync(string localBinaryPath, string expectedMd5, string serviceName, string remoteBinaryPath, CancellationToken cancellationToken = default);
    IAsyncEnumerable<TestSessionEvent> RunSessionAsync(
        string sessionId,
        string sn,
        IReadOnlyList<TestPlanItem> testPlan,
        CancellationToken cancellationToken = default);

    Task SubmitOperatorDecisionAsync(
        string sessionId,
        string testId,
        bool passed,
        CancellationToken cancellationToken = default);

    Task SubmitTestDecisionAsync(
        string sessionId,
        string testId,
        bool passed,
        string reason,
        CancellationToken cancellationToken = default);

    Task SubmitTestControlAsync(string sessionId, string testId, string level, CancellationToken cancellationToken = default);
    Task<CommandResponse> SyncSessionSummaryAsync(
        string sessionId,
        string sn,
        string boardId,
        string finalVerdict,
        IReadOnlyList<TestResultRecord> testResults,
        CancellationToken cancellationToken = default);

    Task<BoardState> GetBoardStateAsync(string sessionId, string sn, CancellationToken cancellationToken = default);
    Task<CommandResponse> WriteSnAsync(string sessionId, string sn, string boardId, CancellationToken cancellationToken = default);
    Task<CommandResponse> EnterTestModeAsync(string sessionId, string sn, string boardId, CancellationToken cancellationToken = default);
    Task<BluetoothScanResult> ScanBluetoothTargetAsync(
        string sessionId,
        string sn,
        string boardId,
        BluetoothScanRequest request,
        CancellationToken cancellationToken = default);
    Task<NetworkPingResult> ConnectWifiAndPingAsync(
        string sessionId,
        string sn,
        string boardId,
        WifiPingRequest request,
        CancellationToken cancellationToken = default);
    Task<NetworkPingResult> ConnectEthernetAndPingAsync(
        string sessionId,
        string sn,
        string boardId,
        EthernetPingRequest request,
        CancellationToken cancellationToken = default);
}
