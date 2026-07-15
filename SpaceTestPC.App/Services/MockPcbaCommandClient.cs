using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class MockPcbaCommandClient : IPcbaCommandClient
{
    private readonly string? _failingTestId;
    private readonly MockConfiguration _mockConfiguration;
    private readonly ManualTestInteractionService? _manualTestInteractionService;
    private string _boardSn = string.Empty;

    public MockPcbaCommandClient(
        string? failingTestId = null,
        MockConfiguration? mockConfiguration = null,
        ManualTestInteractionService? manualTestInteractionService = null)
    {
        _mockConfiguration = mockConfiguration ?? new MockConfiguration();
        _failingTestId = failingTestId ?? _mockConfiguration.FailingTestId;
        _manualTestInteractionService = manualTestInteractionService;
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
                Message = CreateMockRunningMessage(test.Id),
                Data = CreateMockRunningData(test)
            };

            if (test.Id == "hdmi")
            {
                var passed = await WaitForManualDecisionAsync("hdmi", cancellationToken);
                if (!passed)
                {
                    yield return new TestSessionEvent
                    {
                        Event = "test.report",
                        TestId = test.Id,
                        Status = "failed",
                        ResultCode = 4101,
                        Message = "HDMI manual observation failed",
                        Data = new Dictionary<string, object?> { ["manualObserved"] = true, ["operatorConfirmed"] = false }
                    };
                    yield return new TestSessionEvent
                    {
                        Event = "session.completed",
                        TestId = test.Id,
                        Status = "failed",
                        ResultCode = 4101,
                        Message = "Mock session stopped after HDMI manual failure"
                    };
                    yield break;
                }
            }
            else if (test.Id == "lcd")
            {
                var passed = await WaitForManualDecisionAsync("lcd", cancellationToken);
                if (!passed)
                {
                    yield return new TestSessionEvent
                    {
                        Event = "test.report",
                        TestId = test.Id,
                        Status = "failed",
                        ResultCode = 4301,
                        Message = "LCD manual observation failed",
                        Data = new Dictionary<string, object?>
                        {
                            ["manualObserved"] = true,
                            ["operatorConfirmed"] = false,
                            ["pattern"] = "rgb"
                        }
                    };
                    yield return new TestSessionEvent
                    {
                        Event = "session.completed",
                        TestId = test.Id,
                        Status = "failed",
                        ResultCode = 4301,
                        Message = "Mock session stopped after LCD manual failure"
                    };
                    yield break;
                }
            }
            else if (test.Id == "keys")
            {
                var detectedKeys = new List<string>();
                foreach (var key in new[] { "up", "down", "left", "right", "confirm" })
                {
                    await Task.Delay(GetMockRunningDelay(test.Id), cancellationToken);
                    detectedKeys.Add(key);
                    yield return new TestSessionEvent
                    {
                        Event = "test.report",
                        TestId = test.Id,
                        Status = "running",
                        Message = $"Key detected: {key}",
                        Data = new Dictionary<string, object?>
                        {
                            ["inputSubsystem"] = "evdev",
                            ["key"] = key,
                            ["detectedKeys"] = detectedKeys.ToArray(),
                            ["expectedKeys"] = new[] { "up", "down", "left", "right", "confirm" },
                            ["allKeysDetected"] = detectedKeys.Count == 5
                        }
                    };
                }
            }
            else if (test.Id == "typec_fast_charge")
            {
                var passed = await WaitForManualDecisionAsync(test.Id, cancellationToken);
                if (!passed)
                {
                    yield return new TestSessionEvent
                    {
                        Event = "test.report",
                        TestId = test.Id,
                        Status = "failed",
                        ResultCode = 4401,
                        Message = "TYPE-C charging values are outside the configured range",
                        Data = CreateMockResultData(test, sn)
                    };
                    yield return new TestSessionEvent { Event = "session.completed", TestId = test.Id, Status = "failed", ResultCode = 4401, Message = "Mock session stopped after TYPE-C charging failure" };
                    yield break;
                }
            }
            else if (test.Id is "indicator_led" or "fan")
            {
                foreach (var phase in new[] { "high", "low" })
                {
                    yield return new TestSessionEvent
                    {
                        Event = "test.report",
                        TestId = test.Id,
                        Status = "running",
                        Message = $"Voltage output phase: {phase}",
                        Data = new Dictionary<string, object?> { ["phase"] = phase, ["measureRequest"] = true }
                    };
                }
                if (!await WaitForManualDecisionAsync(test.Id, cancellationToken))
                {
                    yield return new TestSessionEvent { Event = "test.report", TestId = test.Id, Status = "failed", ResultCode = 4501, Message = "Voltage verification failed" };
                    yield return new TestSessionEvent { Event = "session.completed", TestId = test.Id, Status = "failed", ResultCode = 4501, Message = "Mock session stopped after voltage verification failure" };
                    yield break;
                }
            }
            else if (test.Id == "battery_management")
            {
                if (!await WaitForManualDecisionAsync(test.Id, cancellationToken))
                {
                    yield return new TestSessionEvent { Event = "test.report", TestId = test.Id, Status = "failed", ResultCode = 4601, Message = "Battery discharge values are outside the configured range" };
                    yield return new TestSessionEvent { Event = "session.completed", TestId = test.Id, Status = "failed", ResultCode = 4601, Message = "Mock session stopped after battery discharge failure" };
                    yield break;
                }
            }
            else if (test.Id == "wifi")
            {
                await Task.Delay(GetMockRunningDelay(test.Id), cancellationToken);
                yield return new TestSessionEvent
                {
                    Event = "test.report", TestId = test.Id, Status = "running", Message = "WiFi connected, pinging router",
                    Data = new Dictionary<string, object?>
                    {
                        ["stage"] = "pinging", ["ssid"] = GetParameterString(test.Parameters, "ssid", "test_router_001"),
                        ["connected"] = true, ["ip"] = "192.168.1.20", ["routerIp"] = GetParameterString(test.Parameters, "routerIp", "192.168.1.1")
                    }
                };
            }
            else if (test.Id == "bluetooth")
            {
                await Task.Delay(GetMockRunningDelay(test.Id), cancellationToken);
                yield return new TestSessionEvent
                {
                    Event = "test.report", TestId = test.Id, Status = "running", Message = "Target Bluetooth advertisement found",
                    Data = new Dictionary<string, object?>
                    {
                        ["stage"] = "target_found", ["mode"] = GetParameterString(test.Parameters, "mode", "observer"),
                        ["targetName"] = GetParameterString(test.Parameters, "targetName", "NODE_A_01"), ["found"] = true, ["rssi"] = -62
                    }
                };
            }
            else
            {
                await Task.Delay(GetMockRunningDelay(test.Id), cancellationToken);
            }

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

            var data = CreateMockResultData(test, sn);

            yield return new TestSessionEvent
            {
                Event = "test.report",
                TestId = test.Id,
                Status = "passed",
                ResultCode = 0,
                Message = CreateMockResultMessage(test.Id),
                Data = data
            };

            await Task.Delay(GetMockResultHoldDelay(test.Id), cancellationToken);
        }

        yield return new TestSessionEvent
        {
            Event = "session.completed",
            Status = "passed",
            ResultCode = 0,
            Message = "Mock session passed"
        };
    }

    public Task SubmitOperatorDecisionAsync(
        string sessionId,
        string testId,
        bool passed,
        CancellationToken cancellationToken = default)
    {
        _manualTestInteractionService?.SubmitDecision(testId, passed);
        return Task.CompletedTask;
    }

    public Task SubmitTestDecisionAsync(string sessionId, string testId, bool passed, string reason, CancellationToken cancellationToken = default)
    {
        _manualTestInteractionService?.SubmitDecision(testId, passed);
        return Task.CompletedTask;
    }

    public Task SubmitTestControlAsync(string sessionId, string testId, string level, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<BoardState> GetBoardStateAsync(string sessionId, string sn, CancellationToken cancellationToken = default)
    {
        var state = new BoardState
        {
            BoardId = "PCB001",
            BoardSn = _boardSn,
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

    public Task<CommandResponse> WriteSnAsync(string sessionId, string sn, string boardId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_boardSn) && !string.Equals(_boardSn, sn, StringComparison.Ordinal))
        {
            return Task.FromResult(new CommandResponse { SessionId = sessionId, Sn = sn, BoardId = boardId, ResultCode = 3001, Message = "SN already programmed", Timestamp = DateTimeOffset.Now.ToString("O") });
        }

        _boardSn = sn;
        return Task.FromResult(new CommandResponse { SessionId = sessionId, Sn = sn, BoardId = boardId, ResultCode = 0, Message = "ok", Timestamp = DateTimeOffset.Now.ToString("O") });
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

    private static Dictionary<string, object?> CreateMockResultData(TestPlanItem test, string sn)
    {
        var testId = test.Id;
        var data = new Dictionary<string, object?>
        {
            ["simulated"] = true,
            ["testId"] = testId,
            ["durationMs"] = 2000
        };

        switch (testId)
        {
            case "board_state":
                data["sn"] = sn;
                data["currentState"] = "idle";
                data["ubootVersion"] = "2024.01";
                data["kernelVersion"] = "6.1.0";
                data["rootfsVersion"] = "1.0.3";
                data["appVersion"] = "0.1.0";
                data["localRecordFound"] = true;
                data["localRecordWritable"] = true;
                data["lastTestVerdict"] = "Pass";
                data["lastTestTime"] = DateTimeOffset.Now.AddMinutes(-30).ToString("O");
                data["passCount"] = 12;
                data["failCount"] = 1;
                data["totalCount"] = 13;
                data["warnings"] = Array.Empty<string>();
                break;
            case "hdmi":
                data["manualObserved"] = true;
                data["resolution"] = "1920x1080";
                data["signalOk"] = true;
                break;
            case "keys":
                data["inputSubsystem"] = "evdev";
                data["expectedKeys"] = 5;
                data["pressedKeys"] = 5;
                data["allKeysOk"] = true;
                break;
            case "lcd":
                data["bus"] = "spi";
                data["pattern"] = "rgb";
                data["backlightOk"] = true;
                data["displayOk"] = true;
                break;
            case "wifi":
                data["ssid"] = GetParameterString(test.Parameters, "ssid", "test_router_001");
                data["routerIp"] = GetParameterString(test.Parameters, "routerIp", "192.168.1.1");
                data["pingCount"] = GetParameterInt(test.Parameters, "pingCount", 4);
                data["connected"] = true;
                data["ip"] = "192.168.1.20";
                data["pingOk"] = true;
                data["avgDelayMs"] = 12;
                break;
            case "bluetooth":
                data["mode"] = GetParameterString(test.Parameters, "mode", "observer");
                data["targetName"] = GetParameterString(test.Parameters, "targetName", "NODE_A_01");
                data["scanWindowMs"] = GetParameterInt(test.Parameters, "scanWindowMs", 10000);
                data["found"] = true;
                data["rssi"] = -62;
                data["minRssi"] = GetParameterInt(test.Parameters, "minRssi", -80);
                break;
            case "fingerprint":
                data["bus"] = GetParameterString(test.Parameters, "bus", "spi");
                data["command"] = GetParameterString(test.Parameters, "command", "get_status");
                data["timeoutMs"] = GetParameterInt(test.Parameters, "timeoutMs", 3000);
                data["communicationOk"] = true;
                data["sensorDetected"] = true;
                break;
            case "typec_fast_charge":
                data["batterySimulationVoltageMv"] = GetParameterInt(test.Parameters, "batterySimulationVoltageMv", 7400);
                data["pmicBus"] = GetParameterString(test.Parameters, "pmicBus", "i2c");
                data["pmicCommunicationOk"] = true;
                data["chargerConnected"] = true;
                data["chargeVoltageMv"] = 8200;
                data["chargeCurrentMa"] = 1850;
                data["stable"] = true;
                break;
            case "typec_camera":
                data["streamProfile"] = GetParameterString(test.Parameters, "streamProfile", "1080p30");
                data["minInterruptCount"] = GetParameterInt(test.Parameters, "minInterruptCount", 200);
                data["timeoutMs"] = GetParameterInt(test.Parameters, "timeoutMs", 10000);
                data["streaming"] = true;
                data["interruptCount"] = 240;
                data["frameRate"] = 30;
                break;
            case "tf":
                data["command"] = GetParameterString(test.Parameters, "command", "read_card_info");
                data["mountTimeoutMs"] = GetParameterInt(test.Parameters, "mountTimeoutMs", 5000);
                data["mounted"] = true;
                data["cardDetected"] = true;
                data["cardName"] = "Mock TF Card";
                data["capacityMb"] = 32768;
                data["fileSystem"] = "exfat";
                data["cardInfoRead"] = true;
                break;
            case "indicator_led":
                data["voltageMeter"] = true;
                data["channelsOk"] = true;
                data["voltageMv"] = 3300;
                break;
            case "fan":
                data["voltageMeter"] = true;
                data["rpm"] = 3200;
                data["pwmPercent"] = 60;
                data["speedOk"] = true;
                break;
            case "otg":
                data["usbDiskDetected"] = true;
                data["writeFileOk"] = true;
                data["readWriteOk"] = true;
                break;
            case "battery_management":
                data["batterySimulationVoltageMv"] = GetParameterInt(test.Parameters, "batterySimulationVoltageMv", 7400);
                data["dischargeVoltageMv"] = 7380;
                data["dischargeCurrentMa"] = 420;
                data["stable"] = true;
                break;
        }

        return data;
    }

    private static string GetParameterString(IReadOnlyDictionary<string, object?> parameters, string key, string fallback)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value is System.Text.Json.JsonElement element && element.ValueKind == System.Text.Json.JsonValueKind.String
            ? element.GetString() ?? fallback
            : value.ToString() ?? fallback;
    }

    private static int GetParameterInt(IReadOnlyDictionary<string, object?> parameters, string key, int fallback)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        if (value is System.Text.Json.JsonElement element && element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
    }

    private static Dictionary<string, object?> CreateMockRunningData(TestPlanItem test)
    {
        var testId = test.Id;
        if (testId == "board_state")
        {
            return new Dictionary<string, object?>
            {
                ["step"] = "reading board info",
                ["readingSn"] = true,
                ["readingVersions"] = true,
                ["readingLocalRecord"] = true
            };
        }

        if (testId == "hdmi")
        {
            return new Dictionary<string, object?>
            {
                ["manualObserved"] = true,
                ["expectedAction"] = "Observe HDMI output and confirm pass or fail"
            };
        }

        if (testId == "lcd")
        {
            return new Dictionary<string, object?>
            {
                ["bus"] = "spi",
                ["pattern"] = "rgb",
                ["expectedAction"] = "Observe backlight and RGB test pattern, then confirm pass or fail"
            };
        }

        if (testId == "typec_fast_charge")
        {
            return new Dictionary<string, object?>
            {
                ["batterySimulationVoltageMv"] = GetParameterInt(test.Parameters, "batterySimulationVoltageMv", 7400),
                ["pmicCommunicationOk"] = true,
                ["chargerConnected"] = true,
                ["chargeVoltageMv"] = 8200,
                ["chargeCurrentMa"] = 1850,
                ["sampleIndex"] = GetParameterInt(test.Parameters, "stableSampleCount", 3),
                ["readyForHostDecision"] = true
            };
        }

        if (testId == "wifi")
        {
            return new Dictionary<string, object?>
            {
                ["stage"] = "connecting", ["ssid"] = GetParameterString(test.Parameters, "ssid", "test_router_001"),
                ["routerIp"] = GetParameterString(test.Parameters, "routerIp", "192.168.1.1")
            };
        }

        if (testId == "bluetooth")
        {
            return new Dictionary<string, object?>
            {
                ["stage"] = "scanning", ["mode"] = GetParameterString(test.Parameters, "mode", "observer"),
                ["targetName"] = GetParameterString(test.Parameters, "targetName", "NODE_A_01"),
                ["minRssi"] = GetParameterInt(test.Parameters, "minRssi", -80)
            };
        }

        if (testId == "battery_management")
        {
            return new Dictionary<string, object?>
            {
                ["batterySimulationVoltageMv"] = GetParameterInt(test.Parameters, "batterySimulationVoltageMv", 7400),
                ["dischargeVoltageMv"] = 7380,
                ["dischargeCurrentMa"] = 420,
                ["sampleIndex"] = GetParameterInt(test.Parameters, "stableSampleCount", 3),
                ["readyForHostDecision"] = true
            };
        }

        return [];
    }

    private async Task<bool> WaitForManualDecisionAsync(string testId, CancellationToken cancellationToken)
    {
        if (_manualTestInteractionService is null)
        {
            await Task.Delay(GetMockRunningDelay(testId), cancellationToken);
            return true;
        }

        return await _manualTestInteractionService.WaitForDecisionAsync(testId, cancellationToken);
    }

    private static string CreateMockRunningMessage(string testId) => testId switch
    {
        "board_state" => "Reading SN, software versions and local test record",
        "hdmi" => "Observe HDMI output and confirm the result manually",
        "lcd" => "Observe SPI LCD RGB pattern and confirm the result manually",
        _ => "Mock test started"
    };

    private TimeSpan GetMockRunningDelay(string testId) => TimeSpan.FromMilliseconds(testId == "board_state"
        ? _mockConfiguration.BoardStateRunningDelayMs
        : _mockConfiguration.RunningDelayMs);

    private TimeSpan GetMockResultHoldDelay(string testId) => TimeSpan.FromMilliseconds(testId == "board_state"
        ? _mockConfiguration.BoardStateResultHoldMs
        : _mockConfiguration.ResultHoldMs);

    private static string CreateMockResultMessage(string testId) => testId switch
    {
        "board_state" => "Board state read",
        "hdmi" => "HDMI signal passed",
        "keys" => "Input subsystem key test passed",
        "lcd" => "SPI LCD test passed",
        "wifi" => "WiFi router ping passed",
        "bluetooth" => "Target Bluetooth name scanned",
        "fingerprint" => "SPI fingerprint module passed",
        "typec_fast_charge" => "TYPE-C fast charge current passed",
        "typec_camera" => "TYPE-C camera stream interrupt test passed",
        "tf" => "TF card info read passed",
        "indicator_led" => "Indicator LED board voltage passed",
        "fan" => "Fan speed passed",
        "otg" => "USB OTG disk read/write passed",
        "battery_management" => "Battery discharge test passed",
        _ => "Mock test passed"
    };
}
