namespace SpaceTestPC.Core.Models;

/// <summary>
/// 老化命令的响应信封，字段与既有 <see cref="CommandResponse"/> 一致，额外带 data。
/// </summary>
public sealed class AgingEnvelope<T>
{
    public string ProtocolVersion { get; init; } = "1.0";
    public string RequestId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Sn { get; init; } = string.Empty;
    public string BoardId { get; init; } = string.Empty;
    public int ResultCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public string Timestamp { get; init; } = string.Empty;
    public T? Data { get; init; }
}
