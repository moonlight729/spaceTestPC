using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class MockPcbaCommandClient : IPcbaCommandClient
{
    public Task EnsureServiceStoppedAsync(string serviceName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ShutdownDeviceAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    private readonly string? _failingTestId;
    private readonly MockConfiguration _mockConfiguration;
    private readonly ManualTestInteractionService? _manualTestInteractionService;
    private string _boardSn = string.Empty;

    public Task<ApplicationMd5Info> GetApplicationMd5Async(string remoteBinaryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ApplicationMd5Info
        {
            AppName = "spacetest3576",
            Path = remoteBinaryPath,
            Md5 = "mock-application-md5",
            Service = "pcba-test.service"
        });

    public Task<ApplicationVersionInfo> GetApplicationVersionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ApplicationVersionInfo { AppName = "spacetest3576", VersionAvailable = false });

    public Task<ApplicationUpgradeResult> UpgradeApplicationAsync(string localBinaryPath, string expectedMd5, string serviceName, string remoteBinaryPath, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ApplicationUpgradeResult { Success = true, FinalMd5 = expectedMd5, Message = "Mock application upgrade completed." });
    public Task<CommandResponse> StartLcdDisplayAsync(string sessionId, string sn, string boardId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommandResponse { SessionId = sessionId, Sn = sn, BoardId = boardId, ResultCode = 0, Message = "Mock LCD display started" });
    public Task<BoardVersions> GetBoardVersionsAsync(string sessionId, string sn, CancellationToken cancellationToken = default) => Task.FromResult(new BoardVersions { UbootVersion = "v0.0.2-260824.095308-e4474f346", KernelVersion = "v0.1.1-260903.050734-436e50b1d", RootfsVersion = "0.0.5", Gen1AppVersion = "0.2.0-20260817-4.5a1eb45a", Gen1AppInstalled = true });

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
        var failedCount = 0;
        var firstFailedCode = 0;
        var firstFailedTestId = string.Empty;
        foreach (var test in testPlan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (test.Skip)
            {
                yield return new TestSessionEvent
                {
                    Event = "test.report",
                    TestId = test.Id,
                    Status = "skipped",
                    ResultCode = 2900,
                    Message = string.IsNullOrWhiteSpace(test.SkipReason) ? "Skipped by host policy" : test.SkipReason,
                    Data = new Dictionary<string, object?>
                    {
                        ["skipReason"] = test.SkipReason ?? "Skipped by host policy",
                        ["countInFinalVerdict"] = false
                    }
                };
                continue;
            }

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
            else if (test.Id == "fingerprint")
            {
                await Task.Delay(GetMockRunningDelay(test.Id), cancellationToken);
                failedCount++;
                if (firstFailedCode == 0)
                {
                    firstFailedCode = 4101;
                    firstFailedTestId = test.Id;
                }
                yield return new TestSessionEvent
                {
                    Event = "test.report",
                    TestId = test.Id,
                    Status = "failed",
                    ResultCode = 4101,
                    Message = "Fingerprint module is not implemented yet",
                    Data = new Dictionary<string, object?> { ["implemented"] = false }
                };
                continue;
            }
            else if (test.Id == "typec_fast_charge")
            {
                yield return new TestSessionEvent
                {
                    Event = "test.report",
                    TestId = test.Id,
                    Status = "running",
                    Message = "Please insert charger",
                    Data = new Dictionary<string, object?>
                    {
                        ["phase"] = "wait_charger",
                        ["chargeControlCommand"] = "enable_charge",
                        ["chargeControlOk"] = true,
                        ["pmicCommunicationOk"] = true,
                        ["chargerConnected"] = false,
                        ["charging"] = false,
                        ["chargeStage"] = "not_charging",
                        ["elapsedMs"] = 0
                    }
                };
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
            else if (test.Id == "ethernet_led")
            {
                yield return new TestSessionEvent
                {
                    Event = "test.report",
                    TestId = test.Id,
                    Status = "running",
                    Message = "Ethernet LED 100M mode; observe green LED",
                    Data = new Dictionary<string, object?>
                    {
                        ["phase"] = "show_100m",
                        ["interfaceName"] = GetParameterString(test.Parameters, "interfaceName", "end0"),
                        ["expectedLed"] = "green",
                        ["phaseDurationMs"] = GetParameterInt(test.Parameters, "phaseDurationMs", 2000),
                        ["timeoutMs"] = GetParameterInt(test.Parameters, "timeoutMs", 15000)
                    }
                };
                yield return new TestSessionEvent
                {
                    Event = "test.report",
                    TestId = test.Id,
                    Status = "running",
                    Message = "Ethernet LED 1000M mode; observe yellow LED",
                    Data = new Dictionary<string, object?>
                    {
                        ["phase"] = "show_1000m",
                        ["interfaceName"] = GetParameterString(test.Parameters, "interfaceName", "end0"),
                        ["expectedLed"] = "yellow",
                        ["phaseDurationMs"] = GetParameterInt(test.Parameters, "phaseDurationMs", 2000),
                        ["timeoutMs"] = GetParameterInt(test.Parameters, "timeoutMs", 15000)
                    }
                };
                if (!await WaitForManualDecisionAsync(test.Id, cancellationToken))
                {
                    yield return new TestSessionEvent { Event = "test.report", TestId = test.Id, Status = "failed", ResultCode = 3910, Message = "Operator confirmed Ethernet LEDs fail" };
                    yield return new TestSessionEvent { Event = "session.completed", TestId = test.Id, Status = "failed", ResultCode = 3015, Message = "Mock session stopped after Ethernet LED failure" };
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
                await Task.Delay(GetMockRunningDelay(test.Id), cancellationToken);
                yield return new TestSessionEvent
                {
                    Event = "test.report",
                    TestId = test.Id,
                    Status = "running",
                    Message = "Battery discharge mode enabled, waiting for host decision",
                    Data = new Dictionary<string, object?>
                    {
                        ["chargeControlCommand"] = "disable_charge",
                        ["chargeControlOk"] = true,
                        ["pmicCommunicationOk"] = true,
                        ["readyForHostDecision"] = true
                    }
                };
                if (!await WaitForManualDecisionAsync(test.Id, cancellationToken))
                {
                    yield return new TestSessionEvent { Event = "test.report", TestId = test.Id, Status = "failed", ResultCode = 4702, Message = "Host confirmed discharge fail" };
                    yield return new TestSessionEvent { Event = "session.completed", TestId = test.Id, Status = "failed", ResultCode = 4702, Message = "Mock session stopped after battery discharge failure" };
                    yield break;
                }
            }
            else if (test.Id == "wifi")
            {
                await Task.Delay(GetMockRunningDelay(test.Id), cancellationToken);
                yield return new TestSessionEvent
                {
                    Event = "test.report", TestId = test.Id, Status = "running", Message = "Wi-Fi scan completed, waiting for host decision",
                    Data = new Dictionary<string, object?>
                    {
                        ["phase"] = "scan_completed",
                        ["ssid"] = GetParameterString(test.Parameters, "ssid", "test_router_001"),
                        ["interfaceName"] = GetParameterString(test.Parameters, "interfaceName", "wlan0"),
                        ["attempt"] = 1,
                        ["maxRetryCount"] = GetParameterInt(test.Parameters, "maxRetryCount", 5),
                        ["readyForHostDecision"] = true,
                        ["found"] = true,
                        ["rssi"] = -62,
                        ["minRssi"] = GetParameterInt(test.Parameters, "minRssi", -75)
                    }
                };
                if (!await WaitForManualDecisionAsync(test.Id, cancellationToken))
                {
                    yield return new TestSessionEvent
                    {
                        Event = "test.report",
                        TestId = test.Id,
                        Status = "failed",
                        ResultCode = 4106,
                        Message = "Host confirmed Wi-Fi RSSI fail",
                        Data = new Dictionary<string, object?>
                        {
                            ["phase"] = "completed",
                            ["ssid"] = GetParameterString(test.Parameters, "ssid", "test_router_001"),
                            ["interfaceName"] = GetParameterString(test.Parameters, "interfaceName", "wlan0"),
                            ["attempt"] = 1,
                            ["maxRetryCount"] = GetParameterInt(test.Parameters, "maxRetryCount", 5),
                            ["found"] = true,
                            ["rssi"] = -62,
                            ["minRssi"] = GetParameterInt(test.Parameters, "minRssi", -75),
                            ["failureReason"] = "rssi_too_low"
                        }
                    };
                    yield return new TestSessionEvent { Event = "session.completed", TestId = test.Id, Status = "failed", ResultCode = 4106, Message = "Mock session stopped after Wi-Fi RSSI failure" };
                    yield break;
                }
            }
            else if (test.Id == "ethernet")
            {
                await Task.Delay(GetMockRunningDelay(test.Id), cancellationToken);
                yield return new TestSessionEvent
                {
                    Event = "test.report", TestId = test.Id, Status = "running", Message = "Insert Ethernet cable",
                    Data = new Dictionary<string, object?>
                    {
                        ["interfaceName"] = GetParameterString(test.Parameters, "interfaceName", "end0"),
                        ["routerIp"] = GetParameterString(test.Parameters, "routerIp", "192.168.110.1"),
                        ["phase"] = "wait_cable",
                        ["ethernetLinkUp"] = false,
                        ["requiresCableInsert"] = true,
                        ["elapsedMs"] = 0
                    }
                };
                await Task.Delay(GetMockRunningDelay(test.Id), cancellationToken);
                yield return new TestSessionEvent
                {
                    Event = "test.report", TestId = test.Id, Status = "running", Message = "Ethernet cable detected",
                    Data = new Dictionary<string, object?>
                    {
                        ["interfaceName"] = GetParameterString(test.Parameters, "interfaceName", "end0"),
                        ["routerIp"] = GetParameterString(test.Parameters, "routerIp", "192.168.110.1"),
                        ["phase"] = "link_up",
                        ["ethernetLinkUp"] = true
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
                        ["phase"] = "scan_started", ["mode"] = GetParameterString(test.Parameters, "mode", "observer"),
                        ["targetName"] = GetParameterString(test.Parameters, "targetName", "NODE_A_01"), ["attempt"] = 1,
                        ["maxRetryCount"] = GetParameterInt(test.Parameters, "maxRetryCount", 5), ["found"] = true,
                        ["rssi"] = -62, ["minRssi"] = GetParameterInt(test.Parameters, "minRssi", -80)
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
            Status = failedCount == 0 ? "passed" : "failed",
            ResultCode = firstFailedCode,
            Message = failedCount == 0 ? "Mock session passed" : $"Mock session completed with {failedCount} failed test(s), first failed: {firstFailedTestId}"
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

    public Task<CommandResponse> SyncSessionSummaryAsync(
        string sessionId,
        string sn,
        string boardId,
        string finalVerdict,
        IReadOnlyList<TestResultRecord> testResults,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new CommandResponse
        {
            SessionId = sessionId,
            Sn = sn,
            BoardId = boardId,
            ResultCode = 0,
            Message = "Mock board summary synced",
            Timestamp = DateTimeOffset.Now.ToString("O")
        });

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
            case "emmc":
            case "ddr":
                data["emmcDevice"] = GetParameterString(test.Parameters, "emmcDevice", "mmcblk0");
                data["emmcName"] = "KIOXIA";
                data["emmcCapacityMiB"] = GetParameterInt(test.Parameters, "emmcMinCapacityGiB", 115) * 1024;
                data["emmcMinCapacityMiB"] = GetParameterInt(test.Parameters, "emmcMinCapacityGiB", 115) * 1024;
                data["emmcTestFileMiB"] = GetParameterInt(test.Parameters, "emmcTestFileMiB", 64);
                data["ddrMemTotalMiB"] = 3900;
                data["ddrMinMemTotalMiB"] = GetParameterInt(test.Parameters, "ddrMinMemTotalMiB", 3200);
                data["ddrStressMiB"] = GetParameterInt(test.Parameters, "ddrStressMiB", 256);
                data["ddrStressLoops"] = GetParameterInt(test.Parameters, "ddrStressLoops", 2);
                data["ddrProcessedMiB"] = GetParameterInt(test.Parameters, "ddrStressMiB", 256) * GetParameterInt(test.Parameters, "ddrStressLoops", 2) * 8;
                data["ddrElapsedMs"] = 1800.0;
                data["ddrThroughputMiBPerSec"] = 227.56;
                data["ddrPatternPass"] = true;
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
                data["interfaceName"] = GetParameterString(test.Parameters, "interfaceName", "wlan0");
                data["attempt"] = 1;
                data["maxRetryCount"] = GetParameterInt(test.Parameters, "maxRetryCount", 5);
                data["found"] = true;
                data["rssi"] = -62;
                data["minRssi"] = GetParameterInt(test.Parameters, "minRssi", -75);
                break;
            case "ethernet":
                data["interfaceName"] = GetParameterString(test.Parameters, "interfaceName", "end0");
                data["routerIp"] = GetParameterString(test.Parameters, "routerIp", "192.168.110.1");
                data["pingCount"] = GetParameterInt(test.Parameters, "pingCount", 4);
                data["ip"] = "192.168.110.220";
                data["pingOk"] = true;
                data["avgDelayMs"] = 3;
                break;
            case "ethernet_led":
                data["interfaceName"] = GetParameterString(test.Parameters, "interfaceName", "end0");
                data["manualObserved"] = true;
                data["displayMode"] = "100m_1000m_led_sequence";
                data["greenLedObserved"] = true;
                data["yellowLedObserved"] = true;
                break;
            case "bluetooth":
                data["mode"] = GetParameterString(test.Parameters, "mode", "observer");
                data["targetName"] = GetParameterString(test.Parameters, "targetName", "NODE_A_01");
                data["scanWindowMs"] = GetParameterInt(test.Parameters, "scanWindowMs", 10000);
                data["attempt"] = 1;
                data["maxRetryCount"] = GetParameterInt(test.Parameters, "maxRetryCount", 5);
                data["found"] = true;
                data["rssi"] = -62;
                data["minRssi"] = GetParameterInt(test.Parameters, "minRssi", -80);
                break;
            case "fingerprint":
                data["bus"] = GetParameterString(test.Parameters, "bus", "spi");
                data["command"] = GetParameterString(test.Parameters, "command", "get_status");
                data["timeoutMs"] = GetParameterInt(test.Parameters, "timeoutMs", 3000);
                data["implemented"] = false;
                break;
            case "typec_fast_charge":
                data["batterySimulationVoltageMv"] = GetParameterInt(test.Parameters, "batterySimulationVoltageMv", 7400);
                data["pmicBus"] = GetParameterString(test.Parameters, "pmicBus", "i2c");
                data["pmicCommunicationOk"] = true;
                data["chargerConnected"] = true;
                data["charging"] = true;
                data["chargeStage"] = "cc";
                data["chargeVoltageMv"] = 8200;
                data["chargeCurrentMa"] = 1850;
                data["stable"] = true;
                data["stableSamples"] = GetParameterInt(test.Parameters, "stableSampleCount", 3);
                break;
            case "typec_camera":
                data["streamProfile"] = GetParameterString(test.Parameters, "streamProfile", "1080p30");
                data["streamFrameCount"] = GetParameterInt(test.Parameters, "streamFrameCount", 90);
                data["minInterruptCount"] = GetParameterInt(test.Parameters, "minInterruptCount", 200);
                data["timeoutMs"] = GetParameterInt(test.Parameters, "timeoutMs", 10000);
                data["streaming"] = true;
                data["interruptCount"] = 240;
                data["frameRate"] = 30;
                data["syncOk"] = true;
                data["syncPulseDelta"] = 90;
                data["syncRate"] = 30.0;
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
            case "usb2":
            case "usb3":
                data["recordFile"] = GetParameterString(test.Parameters, "recordFile", "/tmp/spacetest_usb_ports.json");
                data["usbVersion"] = test.Id == "usb2" ? "usb2" : "usb3";
                data["usb2Count"] = GetParameterInt(test.Parameters, "expectedUsb2Count", 4);
                data["usb3Count"] = GetParameterInt(test.Parameters, "expectedUsb3Count", 4);
                data["requiredCycles"] = 4;
                break;
            case "pcba_test_points":
                var channelCount = GetParameterInt(test.Parameters, "channelCount", 32);
                data["recordFile"] = GetParameterString(test.Parameters, "recordFile", "/tmp/spacetest_pcba_points.json");
                data["channelCount"] = channelCount;
                data["passedCount"] = channelCount;
                data["failedPoints"] = Array.Empty<int>();
                data["defaultMinMv"] = GetParameterInt(test.Parameters, "defaultMinMv", 0);
                data["defaultMaxMv"] = GetParameterInt(test.Parameters, "defaultMaxMv", 5000);
                data["points"] = Enumerable.Range(1, channelCount)
                    .Select(index => new Dictionary<string, object?>
                    {
                        ["index"] = index,
                        ["name"] = $"TP{index:D2}",
                        ["voltageMv"] = 3300,
                        ["minMv"] = GetParameterInt(test.Parameters, "defaultMinMv", 0),
                        ["maxMv"] = GetParameterInt(test.Parameters, "defaultMaxMv", 5000),
                        ["passed"] = true
                    })
                    .ToArray();
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
            var currentSamples = new[] { 498, 505, 512, 507, 501, 690, 496 };
            var voltageSamples = new[] { 8180, 8205, 8196, 8211, 8202, 8194, 8208 };
            return new Dictionary<string, object?>
            {
                ["batterySimulationVoltageMv"] = GetParameterInt(test.Parameters, "batterySimulationVoltageMv", 7400),
                ["pmicCommunicationOk"] = true,
                ["chargerConnected"] = true,
                ["charging"] = true,
                ["chargeStage"] = "cc",
                ["chargeVoltageMv"] = 8200,
                ["chargeCurrentMa"] = 505,
                ["rawVoltageSamplesMv"] = voltageSamples,
                ["rawCurrentSamplesMa"] = currentSamples,
                ["stable"] = true,
                ["stableSamples"] = currentSamples.Length,
                ["sampleIndex"] = currentSamples.Length,
                ["readyForHostDecision"] = true
            };
        }

        if (testId == "wifi")
        {
            return new Dictionary<string, object?>
            {
                ["phase"] = "scan_started",
                ["ssid"] = GetParameterString(test.Parameters, "ssid", "test_router_001"),
                ["interfaceName"] = GetParameterString(test.Parameters, "interfaceName", "wlan0"),
                ["attempt"] = 1,
                ["maxRetryCount"] = GetParameterInt(test.Parameters, "maxRetryCount", 5)
            };
        }

        if (testId == "emmc" || testId == "ddr")
        {
            return new Dictionary<string, object?>
            {
                ["phase"] = "start",
                ["emmcDevice"] = GetParameterString(test.Parameters, "emmcDevice", "mmcblk0"),
                ["emmcMinCapacityMiB"] = GetParameterInt(test.Parameters, "emmcMinCapacityGiB", 115) * 1024,
                ["emmcTestFileMiB"] = GetParameterInt(test.Parameters, "emmcTestFileMiB", 64),
                ["ddrMinMemTotalMiB"] = GetParameterInt(test.Parameters, "ddrMinMemTotalMiB", 3200),
                ["ddrStressMiB"] = GetParameterInt(test.Parameters, "ddrStressMiB", 256),
                ["ddrStressLoops"] = GetParameterInt(test.Parameters, "ddrStressLoops", 2),
                ["ddrProcessedMiB"] = GetParameterInt(test.Parameters, "ddrStressMiB", 256) * GetParameterInt(test.Parameters, "ddrStressLoops", 2) * 8,
                ["ddrElapsedMs"] = 1800.0,
                ["ddrThroughputMiBPerSec"] = 227.56
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
        "emmc" => "Running eMMC device test",
        "ddr" => "Running DDR device test",
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
        "emmc" => "eMMC device test passed",
        "ddr" => "DDR device test passed",
        "hdmi" => "HDMI signal passed",
        "keys" => "Input subsystem key test passed",
        "lcd" => "SPI LCD test passed",
        "ethernet" => "Ethernet cable test passed",
        "ethernet_led" => "Ethernet LED test passed",
        "wifi" => "WiFi RSSI scan passed",
        "bluetooth" => "Target Bluetooth name scanned",
        "fingerprint" => "Fingerprint module is not implemented yet",
        "typec_fast_charge" => "TYPE-C fast charge current passed",
        "typec_camera" => "TYPE-C camera stream interrupt test passed",
        "tf" => "TF card info read passed",
        "usb2" => "USB2.0 record loaded",
        "usb3" => "USB3.0 record loaded",
        "pcba_test_points" => "PCBA test point voltages are in range",
        "indicator_led" => "Indicator LED board voltage passed",
        "fan" => "Fan speed passed",
        "otg" => "USB OTG disk read/write passed",
        "battery_management" => "Battery discharge test passed",
        _ => "Mock test passed"
    };
}
