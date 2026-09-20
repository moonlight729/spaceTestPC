using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SpaceTestPC.Core.Models;

namespace SpaceTestPC.Core.Services;

public sealed class AgingCommandException : Exception
{
    public int ResultCode { get; }

    public AgingCommandException(int resultCode, string message)
        : base(message) => ResultCode = resultCode;
}

/// <summary>
/// 老化命令组的 TCP 客户端。协议沿用既有上位机的"行分隔 JSON + 信封"（§2.3 控制面 19001），
/// 命令组为 aging。
///
/// 每次命令新建一条短连接：老化是长跑，长期持有 32 条连接没有必要，
/// 而且断连重试在短连接模型下更简单。
/// </summary>
public sealed class AgingCommandClient : IAgingCommandClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _host;
    private readonly int _port;
    private readonly int _commandTimeoutMs;

    public string Host => _host;

    public event Action<string>? Log;

    public AgingCommandClient(string host, int port = 19001, int commandTimeoutMs = 15000)
    {
        _host = host;
        _port = port;
        _commandTimeoutMs = commandTimeoutMs;
    }

    public async Task<AgingStartAck> StartAsync(AgingStartRequest request, CancellationToken cancellationToken = default)
    {
        // busy / error 是预期结果之一（§5.5），不能靠异常表达，否则批量下发会被一次 busy 打断。
        var envelope = await SendTolerantAsync<AgingStatusSnapshot>(
            "start", ToParameters(request), _commandTimeoutMs, cancellationToken);

        return new AgingStartAck
        {
            Accepted = envelope.ResultCode == 0,
            RunId = envelope.Data?.RunId ?? request.RunId,
            State = envelope.Data?.State ?? AgingRunStates.Idle,
            Reason = envelope.Message
        };
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await SendTolerantAsync<AgingStatusSnapshot>("stop", null, _commandTimeoutMs, cancellationToken);
        return envelope.ResultCode == 0;
    }

    public async Task<AgingStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await SendAsync<AgingStatusSnapshot>("status", null, _commandTimeoutMs, cancellationToken);
        return envelope.Data ?? new AgingStatusSnapshot { Sn = envelope.Sn };
    }

    public async Task<AgingRunResult?> GetResultAsync(CancellationToken cancellationToken = default)
    {
        // 未结束的设备会返回非 0，这是正常状态而不是错误，因此用 tolerant 通道。
        var envelope = await SendTolerantAsync<AgingRunResult>("result", null, _commandTimeoutMs, cancellationToken);
        return envelope.Data;
    }

    public async Task<AgingCleanupInfo> CleanupAsync(
        string scope = "media", int timeoutMs = 600000, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["scope"] = scope };
        var envelope = await SendTolerantAsync<AgingCleanupInfo>("cleanup", parameters, timeoutMs, cancellationToken);

        return envelope.Data ?? new AgingCleanupInfo
        {
            Ok = false,
            MediaPresent = true,
            DurationSec = 0
        };
    }

    private async Task<AgingEnvelope<T>> SendAsync<T>(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var envelope = await SendTolerantAsync<T>(command, parameters, timeoutMs, cancellationToken);
        if (envelope.ResultCode != 0)
        {
            throw new AgingCommandException(envelope.ResultCode,
                $"aging.{command} 失败：code={envelope.ResultCode}, message={envelope.Message}");
        }

        return envelope;
    }

    private async Task<AgingEnvelope<T>> SendTolerantAsync<T>(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeoutMs);

        var hostCommand = new HostCommand
        {
            CommandGroup = "aging",
            Command = command,
            Parameters = parameters ?? new Dictionary<string, object?>()
        };

        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_host, _port, timeoutSource.Token);
            await using var stream = client.GetStream();

            var requestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(hostCommand, JsonOptions) + "\n");
            Log?.Invoke($"AGING TX {_host}: aging.{command}, bytes={requestBytes.Length}");
            await stream.WriteAsync(requestBytes, timeoutSource.Token);
            await stream.FlushAsync(timeoutSource.Token);

            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var responseLine = await reader.ReadLineAsync(timeoutSource.Token);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                throw new InvalidOperationException($"设备返回空响应：{_host}:{_port} aging.{command}");
            }

            Log?.Invoke($"AGING RX {_host}: {Truncate(responseLine)}");
            return JsonSerializer.Deserialize<AgingEnvelope<T>>(responseLine, JsonOptions)
                   ?? throw new InvalidOperationException($"老化响应反序列化失败：{_host} aging.{command}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"aging.{command} 超时（{timeoutMs} ms）：{_host}:{_port}");
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"无法连接设备 {_host}:{_port}：{ex.SocketErrorCode}", ex);
        }
    }

    private static IReadOnlyDictionary<string, object?> ToParameters(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonOptions)
               ?? new Dictionary<string, object?>();
    }

    private static string Truncate(string value) =>
        value.Length <= 400 ? value : string.Concat(value.AsSpan(0, 400), "...");
}
