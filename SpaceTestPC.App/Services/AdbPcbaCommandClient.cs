using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class AdbPcbaCommandClient : IPcbaCommandClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _adbPath;
    private readonly string? _deviceSerial;
    private readonly int _localPort;
    private readonly int _remotePort;
    private readonly SemaphoreSlim _sessionWriteGate = new(1, 1);
    private NetworkStream? _activeSessionStream;
    private string? _activeSessionId;

    public AdbPcbaCommandClient(string adbPath = "adb", int localPort = 19001, int remotePort = 19001, string? deviceSerial = null)
    {
        _adbPath = adbPath;
        _localPort = localPort;
        _remotePort = remotePort;
        _deviceSerial = deviceSerial;
    }

    public async IAsyncEnumerable<TestSessionEvent> RunSessionAsync(
        string sessionId,
        string sn,
        IReadOnlyList<TestPlanItem> testPlan,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureForwardAsync(cancellationToken);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _localPort, cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

        var command = new HostCommand
        {
            SessionId = sessionId,
            Sn = sn,
            CommandGroup = "session",
            Command = "start",
            Parameters = new Dictionary<string, object?> { ["tests"] = testPlan }
        };

        var requestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, JsonOptions) + "\n");
        await stream.WriteAsync(requestBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        _activeSessionStream = stream;
        _activeSessionId = sessionId;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                {
                    throw new InvalidOperationException("PCBA closed the test-session stream before completion.");
                }

                var testEvent = JsonSerializer.Deserialize<TestSessionEvent>(line, JsonOptions)
                    ?? throw new InvalidOperationException("Failed to parse PCBA test-session event.");
                yield return testEvent;

                if (testEvent.Event == "session.completed")
                {
                    yield break;
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_activeSessionStream, stream))
            {
                _activeSessionStream = null;
                _activeSessionId = null;
            }
        }
    }

    public async Task SubmitOperatorDecisionAsync(
        string sessionId,
        string testId,
        bool passed,
        CancellationToken cancellationToken = default)
    {
        if (_activeSessionStream is null || !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("No active PCBA test session is available for the operator decision.");
        }

        var decision = new
        {
            @event = "operator.decision",
            sessionId,
            testId,
            passed,
            timestamp = DateTimeOffset.Now.ToString("O")
        };

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(decision, JsonOptions) + "\n");
        await _sessionWriteGate.WaitAsync(cancellationToken);
        try
        {
            await _activeSessionStream.WriteAsync(bytes, cancellationToken);
            await _activeSessionStream.FlushAsync(cancellationToken);
        }
        finally
        {
            _sessionWriteGate.Release();
        }
    }

    public Task SubmitTestDecisionAsync(string sessionId, string testId, bool passed, string reason, CancellationToken cancellationToken = default) =>
        SubmitSessionDecisionAsync("test.decision", "host_auto", sessionId, testId, passed, reason, cancellationToken);

    public Task SubmitTestControlAsync(string sessionId, string testId, string level, CancellationToken cancellationToken = default) =>
        SubmitSessionControlAsync(sessionId, testId, level, cancellationToken);

    private async Task SubmitSessionControlAsync(string sessionId, string testId, string level, CancellationToken cancellationToken)
    {
        if (_activeSessionStream is null || !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)) throw new InvalidOperationException("No active PCBA test session is available for test control.");
        var command = new { @event = "test.control", sessionId, testId, command = "set_output_level", level, timestamp = DateTimeOffset.Now.ToString("O") };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, JsonOptions) + "\n");
        await _sessionWriteGate.WaitAsync(cancellationToken);
        try { await _activeSessionStream.WriteAsync(bytes, cancellationToken); await _activeSessionStream.FlushAsync(cancellationToken); }
        finally { _sessionWriteGate.Release(); }
    }

    private async Task SubmitSessionDecisionAsync(string eventName, string source, string sessionId, string testId, bool passed, string reason, CancellationToken cancellationToken)
    {
        if (_activeSessionStream is null || !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("No active PCBA test session is available for the test decision.");
        }

        var decision = new { @event = eventName, source, sessionId, testId, passed, reason, timestamp = DateTimeOffset.Now.ToString("O") };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(decision, JsonOptions) + "\n");
        await _sessionWriteGate.WaitAsync(cancellationToken);
        try { await _activeSessionStream.WriteAsync(bytes, cancellationToken); await _activeSessionStream.FlushAsync(cancellationToken); }
        finally { _sessionWriteGate.Release(); }
    }

    public async Task<BoardState> GetBoardStateAsync(string sessionId, string sn, CancellationToken cancellationToken = default)
    {
        var command = new HostCommand
        {
            SessionId = sessionId,
            Sn = sn,
            CommandGroup = "sys",
            Command = "get_board_state"
        };

        var payload = await SendCommandAsync(command, cancellationToken);
        var response = DeserializeEnvelope<BoardStateEnvelope>(payload);

        return new BoardState
        {
            BoardId = response.Data.BoardId,
            BoardSn = response.Data.BoardSn,
            TestMode = response.Data.TestMode,
            CurrentState = response.Data.CurrentState,
            LastSessionId = response.Data.LastSessionId,
            LastStartTime = response.Data.LastStartTime,
            LastEndTime = response.Data.LastEndTime,
            LastVerdict = response.Data.LastVerdict,
            PassCount = response.Data.PassCount,
            FailCount = response.Data.FailCount,
            TotalCount = response.Data.TotalCount,
            Version = response.Data.Version
        };
    }

    public Task<CommandResponse> EnterTestModeAsync(string sessionId, string sn, string boardId, CancellationToken cancellationToken = default) =>
        SendSystemCommandAsync(sessionId, sn, boardId, "enter_test_mode", cancellationToken);

    public Task<CommandResponse> WriteSnAsync(string sessionId, string sn, string boardId, CancellationToken cancellationToken = default) =>
        SendSystemCommandAsync(sessionId, sn, boardId, "write_sn", cancellationToken,
            new Dictionary<string, object?> { ["verifyReadBack"] = true });

    private async Task<CommandResponse> SendSystemCommandAsync(
        string sessionId,
        string sn,
        string boardId,
        string commandName,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var command = new HostCommand
        {
            SessionId = sessionId,
            Sn = sn,
            BoardId = boardId,
            CommandGroup = "sys",
            Command = commandName,
            Parameters = parameters ?? new Dictionary<string, object?>()
        };

        var payload = await SendCommandAsync(command, cancellationToken);
        var response = DeserializeEnvelope<Dictionary<string, object?>>(payload);

        return new CommandResponse
        {
            RequestId = response.RequestId,
            SessionId = response.SessionId,
            Sn = sn,
            BoardId = boardId,
            ResultCode = response.ResultCode,
            Message = response.Message,
            Timestamp = response.Timestamp
        };
    }

    public async Task<BluetoothScanResult> ScanBluetoothTargetAsync(
        string sessionId,
        string sn,
        string boardId,
        BluetoothScanRequest request,
        CancellationToken cancellationToken = default)
    {
        var command = new HostCommand
        {
            SessionId = sessionId,
            Sn = sn,
            BoardId = boardId,
            CommandGroup = "bt",
            Command = "scan_target_name",
            Parameters = new Dictionary<string, object?>
            {
                ["targetName"] = request.TargetName,
                ["timeoutMs"] = request.TimeoutMs,
                ["minRssi"] = request.MinRssi
            }
        };

        var payload = await SendCommandAsync(command, cancellationToken);
        var response = DeserializeEnvelope<BluetoothScanResult>(payload);
        return response.Data;
    }

    public async Task<NetworkPingResult> ConnectWifiAndPingAsync(
        string sessionId,
        string sn,
        string boardId,
        WifiPingRequest request,
        CancellationToken cancellationToken = default)
    {
        var command = new HostCommand
        {
            SessionId = sessionId,
            Sn = sn,
            BoardId = boardId,
            CommandGroup = "wifi",
            Command = "connect_and_ping",
            Parameters = new Dictionary<string, object?>
            {
                ["ssid"] = request.Ssid,
                ["password"] = request.Password,
                ["targetIp"] = request.TargetIp,
                ["pingCount"] = request.PingCount,
                ["timeoutMs"] = request.TimeoutMs
            }
        };

        var payload = await SendCommandAsync(command, cancellationToken);
        var response = DeserializeEnvelope<NetworkPingResult>(payload);
        return response.Data;
    }

    public async Task<NetworkPingResult> ConnectEthernetAndPingAsync(
        string sessionId,
        string sn,
        string boardId,
        EthernetPingRequest request,
        CancellationToken cancellationToken = default)
    {
        var command = new HostCommand
        {
            SessionId = sessionId,
            Sn = sn,
            BoardId = boardId,
            CommandGroup = "eth",
            Command = "connect_and_ping",
            Parameters = new Dictionary<string, object?>
            {
                ["routerIp"] = request.RouterIp,
                ["targetIp"] = request.TargetIp,
                ["pingCount"] = request.PingCount,
                ["timeoutMs"] = request.TimeoutMs
            }
        };

        var payload = await SendCommandAsync(command, cancellationToken);
        var response = DeserializeEnvelope<NetworkPingResult>(payload);
        return response.Data;
    }

    private async Task<string> SendCommandAsync(HostCommand command, CancellationToken cancellationToken)
    {
        await EnsureForwardAsync(cancellationToken);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", _localPort, cancellationToken);

        await using var stream = client.GetStream();

        // The PCBA side reads line-delimited JSON, so each request ends with a newline.
        var requestJson = JsonSerializer.Serialize(command, JsonOptions) + "\n";
        var requestBytes = Encoding.UTF8.GetBytes(requestJson);
        await stream.WriteAsync(requestBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var responseLine = await reader.ReadLineAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(responseLine))
        {
            throw new InvalidOperationException("ADB forward connected, but the PCBA service returned an empty response.");
        }

        return responseLine;
    }

    private async Task EnsureForwardAsync(CancellationToken cancellationToken)
    {
        var arguments = BuildAdbArguments($"forward tcp:{_localPort} tcp:{_remotePort}");

        var startInfo = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdOut = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ADB forward failed with exit code {process.ExitCode}. stdout: {stdOut} stderr: {stdErr}");
        }
    }

    private string BuildAdbArguments(string command)
    {
        return string.IsNullOrWhiteSpace(_deviceSerial)
            ? command
            : $"-s {_deviceSerial} {command}";
    }

    private static ResponseEnvelope<T> DeserializeEnvelope<T>(string payload)
    {
        var envelope = JsonSerializer.Deserialize<ResponseEnvelope<T>>(payload, JsonOptions);
        if (envelope is null)
        {
            throw new InvalidOperationException("Failed to parse PCBA response payload.");
        }

        return envelope;
    }

    private sealed class ResponseEnvelope<T>
    {
        public string ProtocolVersion { get; init; } = "1.0";
        public string RequestId { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public int ResultCode { get; init; }
        public string Message { get; init; } = string.Empty;
        public T Data { get; init; } = default!;
        public string Timestamp { get; init; } = string.Empty;
    }

    private sealed class BoardStateEnvelope
    {
        public string BoardId { get; init; } = string.Empty;
        public string BoardSn { get; init; } = string.Empty;
        public string TestMode { get; init; } = "ready";
        public string CurrentState { get; init; } = "idle";
        public string LastSessionId { get; init; } = string.Empty;
        public string LastStartTime { get; init; } = string.Empty;
        public string LastEndTime { get; init; } = string.Empty;
        public string LastVerdict { get; init; } = string.Empty;
        public int PassCount { get; init; }
        public int FailCount { get; init; }
        public int TotalCount { get; init; }
        public int Version { get; init; } = 1;
    }
}
