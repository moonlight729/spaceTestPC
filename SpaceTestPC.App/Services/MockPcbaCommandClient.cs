using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class MockPcbaCommandClient : IPcbaCommandClient
{
    private readonly string? _failingTestId;

    public MockPcbaCommandClient(string? failingTestId = null)
    {
        _failingTestId = failingTestId;
    }

    public async IAsyncEnumerable<TestSessionEvent> RunSessionAsync(
        string sessionId,
        string sn,
        IReadOnlyList<TestPlanItem> testPlan,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var test in testPlan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new TestSessionEvent
            {
                Event = "test.report",
                TestId = test.Id,
                Status = "running",
                Message = "Mock test started"
            };

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            if (test.Id == _failingTestId)
            {
                yield return new TestSessionEvent
                {
                    Event = "test.report",
                    TestId = test.Id,
                    Status = "failed",
                    ResultCode = 4001,
                    Message = "Mock test failed"
                };
                yield return new TestSessionEvent
                {
                    Event = "session.completed",
                    TestId = test.Id,
                    Status = "failed",
                    ResultCode = 4001,
                    Message = "Mock session stopped after test failure"
                };
                yield break;
            }

            var data = CreateMockResultData(test.Id);

            yield return new TestSessionEvent
            {
                Event = "test.report",
                TestId = test.Id,
                Status = "passed",
                ResultCode = 0,
                Message = CreateMockResultMessage(test.Id),
                Data = data
            };
        }

        yield return new TestSessionEvent
        {
            Event = "session.completed",
            Status = "passed",
            ResultCode = 0,
            Message = "Mock session passed"
        };
    }

    public Task<BoardState> GetBoardStateAsync(string sessionId, string sn, CancellationToken cancellationToken = default)
    {
        var state = new BoardState
        {
            BoardId = "PCB001",
            BoardSn = sn,
            TestMode = "ready",
            CurrentState = "idle",
            LastSessionId = sessionId,
            LastStartTime = DateTimeOffset.Now.AddMinutes(-5).ToString("O"),
            LastEndTime = string.Empty,
            LastVerdict = "Pending",
            PassCount = 0,
            FailCount = 0,
            TotalCount = 0,
            Version = 1
        };

        return Task.FromResult(state);
    }

    public Task<CommandResponse> EnterTestModeAsync(string sessionId, string sn, string boardId, CancellationToken cancellationToken = default)
    {
        var response = new CommandResponse
        {
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Sn = sn,
            BoardId = boardId,
            ResultCode = 0,
            Message = "ok",
            Timestamp = DateTimeOffset.Now.ToString("O")
        };

        return Task.FromResult(response);
    }

    public Task<BluetoothScanResult> ScanBluetoothTargetAsync(
        string sessionId,
        string sn,
        string boardId,
        BluetoothScanRequest request,
        CancellationToken cancellationToken = default)
    {
        // Mock mode intentionally returns stable data so the UI flow can be exercised without hardware.
        var result = new BluetoothScanResult
        {
            Found = true,
            TargetName = request.TargetName,
            Rssi = -62
        };

        return Task.FromResult(result);
    }

    public Task<NetworkPingResult> ConnectWifiAndPingAsync(
        string sessionId,
        string sn,
        string boardId,
        WifiPingRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = new NetworkPingResult
        {
            Connected = true,
            Linked = true,
            Ip = "192.168.1.20",
            PingOk = true,
            AvgDelayMs = 12
        };

        return Task.FromResult(result);
    }

    public Task<NetworkPingResult> ConnectEthernetAndPingAsync(
        string sessionId,
        string sn,
        string boardId,
        EthernetPingRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = new NetworkPingResult
        {
            Connected = true,
            Linked = true,
            Ip = "192.168.1.30",
            PingOk = true,
            AvgDelayMs = 8
        };

        return Task.FromResult(result);
    }

    private static Dictionary<string, object?> CreateMockResultData(string testId)
    {
        var data = new Dictionary<string, object?>
        {
            ["simulated"] = true,
            ["testId"] = testId,
            ["durationMs"] = 2000
        };

        switch (testId)
        {
            case "board_state":
                data["boardId"] = "PCB001";
                data["currentState"] = "idle";
                data["snMatched"] = true;
                data["firmware"] = "1.0.0";
                break;
            case "test_mode":
                data["entered"] = true;
                data["mode"] = "factory";
                break;
            case "bluetooth":
                data["targetName"] = "NODE_A_01";
                data["found"] = true;
                data["rssi"] = -62;
                data["minRssi"] = -80;
                break;
            case "wifi":
                data["ssid"] = "FactoryAP";
                data["connected"] = true;
                data["ip"] = "192.168.1.20";
                data["pingOk"] = true;
                data["avgDelayMs"] = 12;
                break;
            case "ethernet":
                data["linked"] = true;
                data["routerIp"] = "192.168.1.1";
                data["targetIp"] = "192.168.1.1";
                data["pingOk"] = true;
                data["avgDelayMs"] = 8;
                break;
            case "tf":
                data["mounted"] = true;
                data["capacityMb"] = 32768;
                data["readWriteOk"] = true;
                break;
            case "lcd":
                data["pattern"] = "rgb";
                data["backlightOk"] = true;
                data["touchOk"] = true;
                break;
            case "fingerprint":
                data["sensorDetected"] = true;
                data["enrollCheckOk"] = true;
                break;
            case "keys":
                data["expectedKeys"] = 4;
                data["pressedKeys"] = 4;
                data["allKeysOk"] = true;
                break;
            case "hdmi":
                data["plugDetected"] = true;
                data["resolution"] = "1920x1080";
                data["signalOk"] = true;
                break;
            case "typec":
                data["ccDetected"] = true;
                data["powerRole"] = "sink";
                data["dataOk"] = true;
                break;
            case "battery":
                data["voltageMv"] = 3820;
                data["charging"] = true;
                data["temperatureC"] = 31;
                break;
            case "fan":
                data["rpm"] = 3200;
                data["pwmPercent"] = 60;
                data["speedOk"] = true;
                break;
            case "otg":
                data["deviceDetected"] = true;
                data["readWriteOk"] = true;
                break;
            case "camera":
                data["deviceDetected"] = true;
                data["frameCaptured"] = true;
                data["resolution"] = "1280x720";
                break;
        }

        return data;
    }

    private static string CreateMockResultMessage(string testId) => testId switch
    {
        "board_state" => "Board state read",
        "test_mode" => "Factory test mode entered",
        "bluetooth" => "Target broadcast found",
        "wifi" => "WiFi connected and ping passed",
        "ethernet" => "Ethernet link and ping passed",
        "tf" => "TF card read/write passed",
        "lcd" => "LCD pattern and touch passed",
        "fingerprint" => "Fingerprint sensor passed",
        "keys" => "All keys passed",
        "hdmi" => "HDMI signal passed",
        "typec" => "Type-C detection passed",
        "battery" => "Battery status passed",
        "fan" => "Fan speed passed",
        "otg" => "OTG read/write passed",
        "camera" => "Camera capture passed",
        _ => "Mock test passed"
    };
}
