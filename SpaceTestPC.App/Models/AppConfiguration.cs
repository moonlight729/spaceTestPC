using System.Text.Json;

namespace SpaceTestPC.App.Models;

public sealed class AppConfiguration
{
    public string TestMode { get; set; } = "finished_product";
    public Dictionary<string, TestModeConfiguration> TestModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public TestPlanConfiguration TestPlan { get; set; } = new();
    public Jk5506Configuration Jk5506 { get; set; } = new();
    public JxTvmConfiguration JxTvm { get; set; } = new();
    public BluetoothBroadcasterConfiguration BluetoothBroadcaster { get; set; } = new();
    public LoggingConfiguration Logging { get; set; } = new();
    public UpgradeConfiguration Upgrade { get; set; } = new();
    public PcbaConnectionConfiguration PcbaConnection { get; set; } = new();
}

public sealed class PcbaConnectionConfiguration
{
    public string Mode { get; set; } = "adbForward";
    public string Host { get; set; } = "auto";
    public int Port { get; set; } = 19001;
    public string AdbPath { get; set; } = "adb";
    public string AdbDeviceSerial { get; set; } = string.Empty;
    public PcbaDiscoveryConfiguration Discovery { get; set; } = new();
}

public sealed class PcbaDiscoveryConfiguration
{
    public bool Enabled { get; set; } = true;
    public string Subnet { get; set; } = "auto";
    public string StartIp { get; set; } = string.Empty;
    public string EndIp { get; set; } = string.Empty;
    public int PingTimeoutMs { get; set; } = 300;
    public int ConnectTimeoutMs { get; set; } = 500;
    public int MaxParallel { get; set; } = 32;
}

public sealed class TestModeConfiguration
{
    public string DisplayName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public string[] EnabledTests { get; set; } = [];
    public string[] DisabledTests { get; set; } = [];
    public string[] TestOrder { get; set; } = [];
    public Dictionary<string, string> SkippedTests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UpgradeConfiguration
{
    public bool Enabled { get; set; } = true;
    public string Transport { get; set; } = "auto";
    public string LocalBinaryPath { get; set; } = "spacetest3576";
    public string RemoteBinaryPath { get; set; } = "/vendor/originflow/bin/spacetest3576";
    public string ServiceName { get; set; } = "pcba-test.service";
    public int AutoUpgradeDelaySeconds { get; set; } = 5;
    public string ApplicationVersion { get; set; } = string.Empty;
    public string SshUser { get; set; } = "originflow";
    public string SshPassword { get; set; } = string.Empty;
    public int SshPort { get; set; } = 22;
    public string SshPath { get; set; } = "ssh";
    public string ScpPath { get; set; } = "scp";
}

public sealed class ApplicationMd5Info
{
    public string AppName { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Md5 { get; init; } = string.Empty;
    public string Service { get; init; } = string.Empty;
    public string DeviceVersion { get; init; } = string.Empty;
    public bool VersionAvailable { get; init; }
}

public sealed class ApplicationUpgradeResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string FinalMd5 { get; init; } = string.Empty;
}

public sealed class ApplicationVersionInfo
{
    public string AppName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public bool VersionAvailable { get; init; }
    public string Path { get; init; } = string.Empty;
}

public sealed class LoggingConfiguration
{
    public bool FileEnabled { get; set; } = true;
    public string FilePath { get; set; } = "logs/space-test-pc.log";
    public string EventFilePath { get; set; } = "logs/space-test-events.jsonl";
}

public sealed class BluetoothBroadcasterConfiguration
{
    public bool Enabled { get; set; } = true;
    public string PortName { get; set; } = "COM6";
    public string BroadcastName { get; set; } = "NODE_A_01";
}

public sealed class JxTvmConfiguration
{
    public bool Enabled { get; set; }
    public string PortName { get; set; } = "COM6";
}

public sealed class Jk5506Configuration
{
    public bool Enabled { get; set; } = true;
    public string PortName { get; set; } = "COM5";
    public int BaudRate { get; set; } = 115200;
    public byte SlaveAddress { get; set; } = 1;
    public int TimeoutMs { get; set; } = 1000;
}

public sealed class TestPlanConfiguration
{
    public bool AllowSnMismatchForDebug { get; set; }
    public string[] EnabledTests { get; set; } = [];
    public string[] DisabledTests { get; set; } = [];
    public Dictionary<string, string> SkippedTests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, JsonElement>> TestParameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public MockConfiguration Mock { get; set; } = new();
    public ContinuousTestConfiguration Continuous { get; set; } = new();
}

public sealed class MockConfiguration
{
    public string? FailingTestId { get; set; }
    public int RunningDelayMs { get; set; } = 2000;
    public int ResultHoldMs { get; set; } = 1000;
    public int BoardStateRunningDelayMs { get; set; } = 3000;
    public int BoardStateResultHoldMs { get; set; } = 5000;
}

public sealed class ContinuousTestConfiguration
{
    public bool EnabledByDefault { get; set; }
}
