namespace SpaceTestPC.Core.Models;

/// <summary>
/// 老化状态机的状态名（§5.3）。用字符串而非枚举：板端仍在同步开发，
/// 新增状态时反序列化不应失败。
/// </summary>
public static class AgingRunStates
{
    public const string Idle = "IDLE";
    public const string Preparing = "PREPARING";
    public const string Running = "RUNNING";
    public const string Cleaning = "CLEANING";
    public const string WaitingManualCheck = "WAITING_MANUAL_CHECK";
    public const string Passed = "PASSED";
    public const string Failed = "FAILED";

    public static bool IsTerminal(string? state) =>
        string.Equals(state, Passed, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, Failed, StringComparison.OrdinalIgnoreCase);

    public static bool IsActive(string? state) =>
        string.Equals(state, Preparing, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, Running, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(state, Cleaning, StringComparison.OrdinalIgnoreCase);
}
