using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class AdbPcbaCommandClient : IPcbaCommandClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _adbPath;
    private readonly string? _deviceSerial;
    private readonly int _localPort;
    private readonly int _remotePort;
    private readonly string _tcpHost;
    private readonly int _tcpPort;
    private readonly bool _useAdbForward;
    private readonly PcbaDiscoveryService? _discoveryService;
    private readonly string _upgradeTransport;
    private readonly string _sshUser;
    private readonly string _sshPassword;
    private readonly int _sshPort;
    private readonly string _sshPath;
    private readonly string _scpPath;
    private readonly SemaphoreSlim _sessionWriteGate = new(1, 1);
    private NetworkStream? _activeSessionStream;
    private string? _activeSessionId;
    public event Action<string>? Log;

    public AdbPcbaCommandClient(
        string adbPath = "adb",
        int localPort = 19001,
        int remotePort = 19001,
        string? deviceSerial = null,
        bool useAdbForward = true,
        string tcpHost = "127.0.0.1",
        int tcpPort = 19001,
        PcbaDiscoveryService? discoveryService = null,
        UpgradeConfiguration? upgradeConfiguration = null)
    {
        _adbPath = adbPath;
        _localPort = localPort;
        _remotePort = remotePort;
        _deviceSerial = deviceSerial;
        _useAdbForward = useAdbForward;
        _tcpHost = string.IsNullOrWhiteSpace(tcpHost) ? "127.0.0.1" : tcpHost.Trim();
        _tcpPort = tcpPort > 0 ? tcpPort : remotePort;
        _discoveryService = discoveryService;
        _upgradeTransport = string.IsNullOrWhiteSpace(upgradeConfiguration?.Transport)
            ? "auto"
            : upgradeConfiguration.Transport.Trim();
        _sshUser = string.IsNullOrWhiteSpace(upgradeConfiguration?.SshUser)
            ? "originflow"
            : upgradeConfiguration.SshUser.Trim();
        _sshPassword = upgradeConfiguration?.SshPassword ?? string.Empty;
        _sshPort = upgradeConfiguration?.SshPort > 0 ? upgradeConfiguration.SshPort : 22;
        _sshPath = string.IsNullOrWhiteSpace(upgradeConfiguration?.SshPath)
            ? "ssh"
            : upgradeConfiguration.SshPath.Trim();
        _scpPath = string.IsNullOrWhiteSpace(upgradeConfiguration?.ScpPath)
            ? "scp"
            : upgradeConfiguration.ScpPath.Trim();
    }

    public async Task<ApplicationMd5Info> GetApplicationMd5Async(string remoteBinaryPath, CancellationToken cancellationToken = default)
    {
        string md5;
        try
        {
            var output = ShouldUseSshScpUpgrade()
                ? await RunSshAsync($"md5sum {ShellQuote(remoteBinaryPath)}", cancellationToken)
                : await RunAdbAsync($"shell md5sum {Quote(remoteBinaryPath)}", cancellationToken);
            md5 = ParseMd5(output);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
        {
            md5 = string.Empty;
        }

        return new ApplicationMd5Info
        {
            AppName = "spacetest3576",
            Path = remoteBinaryPath,
            Md5 = md5,
            Service = "pcba-test.service"
        };
    }

    public async Task<ApplicationVersionInfo> GetApplicationVersionAsync(CancellationToken cancellationToken = default)
    {
        var command = new HostCommand { SessionId = "version-check", CommandGroup = "sys", Command = "get_version" };
        var payload = await SendCommandAsync(command, cancellationToken);
        var response = DeserializeEnvelope<ApplicationVersionInfo>(payload);
        if (response.ResultCode != 0)
            return new ApplicationVersionInfo { VersionAvailable = false };
        return response.Data ?? new ApplicationVersionInfo { VersionAvailable = false };
    }

    public async Task<ApplicationUpgradeResult> UpgradeApplicationAsync(
        string localBinaryPath, string expectedMd5, string serviceName, string remoteBinaryPath,
        CancellationToken cancellationToken = default)
    {
        if (ShouldUseSshScpUpgrade())
        {
            return await UpgradeApplicationOverSshScpAsync(localBinaryPath, expectedMd5, serviceName, remoteBinaryPath, cancellationToken);
        }

        var remoteNewPath = remoteBinaryPath + ".new";
        var remoteBackupPath = remoteBinaryPath + ".bak";
        var hasBackup = false;
        var totalTimer = Stopwatch.StartNew();
        var timing = new List<string>();
        try
        {
            var localMd5 = await CalculateMd5Async(localBinaryPath, cancellationToken);
            if (!string.Equals(localMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
                return new ApplicationUpgradeResult { Message = "Local application MD5 changed before upload." };

            var timer = Stopwatch.StartNew();
            await RunAdbAsync($"push {Quote(localBinaryPath)} {Quote(remoteNewPath)}", cancellationToken);
            timing.Add($"push={timer.ElapsedMilliseconds}ms");

            timer.Restart();
            var upgradeScript = string.Join("; ",
                "set -e",
                $"chmod 755 {Quote(remoteNewPath)}",
                $"uploaded_md5=$(md5sum {Quote(remoteNewPath)} | awk '{{print $1}}')",
                $"[ \"$uploaded_md5\" = \"{expectedMd5}\" ]",
                $"systemctl stop {Quote(serviceName)}",
                $"if [ -f {Quote(remoteBinaryPath)} ]; then cp -p {Quote(remoteBinaryPath)} {Quote(remoteBackupPath)}; fi",
                $"mv -f {Quote(remoteNewPath)} {Quote(remoteBinaryPath)}",
                $"chmod 755 {Quote(remoteBinaryPath)}",
                $"systemctl start {Quote(serviceName)}",
                $"for i in $(seq 1 20); do systemctl is-active --quiet {Quote(serviceName)} && break; sleep 0.1; done",
                $"systemctl is-active --quiet {Quote(serviceName)}",
                $"final_md5=$(md5sum {Quote(remoteBinaryPath)} | awk '{{print $1}}')",
                "printf 'UPLOADED_MD5=%s\\nFINAL_MD5=%s\\n' \"$uploaded_md5\" \"$final_md5\"");
            var upgradeOutput = await RunAdbAsync($"shell sh -c {Quote(upgradeScript)}", cancellationToken);
            timing.Add($"remoteUpgrade={timer.ElapsedMilliseconds}ms");
            var uploadedMd5 = ParseTaggedMd5(upgradeOutput, "UPLOADED_MD5");
            var finalMd5 = ParseTaggedMd5(upgradeOutput, "FINAL_MD5");
            if (!string.Equals(uploadedMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
                return new ApplicationUpgradeResult { Message = "Uploaded application MD5 verification failed." };
            hasBackup = true;
            if (!string.Equals(finalMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Final application MD5 verification failed.");

            timing.Add($"total={totalTimer.ElapsedMilliseconds}ms");
            return new ApplicationUpgradeResult
            {
                Success = true,
                FinalMd5 = finalMd5,
                Message = $"Application upgrade completed. Timing: {string.Join(", ", timing)}"
            };
        }
        catch (Exception ex)
        {
            try
            {
                await RunAdbAsync($"shell systemctl stop {Quote(serviceName)}", cancellationToken);
                if (hasBackup)
                    await RunAdbAsync($"shell mv -f {Quote(remoteBackupPath)} {Quote(remoteBinaryPath)}", cancellationToken);
                await RunAdbAsync($"shell chmod 755 {Quote(remoteBinaryPath)}", cancellationToken);
                await RunAdbAsync($"shell systemctl start {Quote(serviceName)}", cancellationToken);
            }
            catch (Exception rollbackError)
            {
                return new ApplicationUpgradeResult { Message = $"Upgrade failed: {ex.Message}; rollback failed: {rollbackError.Message}; Timing: {string.Join(", ", timing)}, total={totalTimer.ElapsedMilliseconds}ms" };
            }
            return new ApplicationUpgradeResult { Message = $"Upgrade failed and was rolled back: {ex.Message}; Timing: {string.Join(", ", timing)}, total={totalTimer.ElapsedMilliseconds}ms" };
        }
    }

    private async Task<string> RunAdbAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _adbPath, Arguments = BuildAdbArguments(arguments),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException($"ADB command failed: {stderr.Trim()} {stdout.Trim()}".Trim());
        return stdout;
    }

    private async Task<ApplicationUpgradeResult> UpgradeApplicationOverSshScpAsync(
        string localBinaryPath,
        string expectedMd5,
        string serviceName,
        string remoteBinaryPath,
        CancellationToken cancellationToken)
    {
        var remoteFileName = Path.GetFileName(remoteBinaryPath);
        var remoteNewPath = $"/tmp/{remoteFileName}.{Guid.NewGuid():N}.new";
        var remoteBackupPath = remoteBinaryPath + ".bak";
        var hasBackup = false;
        var totalTimer = Stopwatch.StartNew();
        var timing = new List<string>();

        try
        {
            var host = await ResolveTcpHostAsync(cancellationToken);
            var localMd5 = await CalculateMd5Async(localBinaryPath, cancellationToken);
            if (!string.Equals(localMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                return new ApplicationUpgradeResult { Message = "Local application MD5 changed before upload." };
            }

            var timer = Stopwatch.StartNew();
            await RunScpAsync(localBinaryPath, host, remoteNewPath, cancellationToken);
            timing.Add($"scp={timer.ElapsedMilliseconds}ms");

            timer.Restart();
            var upgradeScript = string.Join("; ",
                "set -e",
                $"chmod 755 {ShellQuote(remoteNewPath)}",
                $"uploaded_md5=$(md5sum {ShellQuote(remoteNewPath)} | awk '{{print $1}}')",
                $"[ \"$uploaded_md5\" = \"{expectedMd5}\" ]",
                $"systemctl stop {ShellQuote(serviceName)}",
                $"if [ -f {ShellQuote(remoteBinaryPath)} ]; then cp -p {ShellQuote(remoteBinaryPath)} {ShellQuote(remoteBackupPath)}; fi",
                $"mv -f {ShellQuote(remoteNewPath)} {ShellQuote(remoteBinaryPath)}",
                $"chmod 755 {ShellQuote(remoteBinaryPath)}",
                $"systemctl start {ShellQuote(serviceName)}",
                $"for i in $(seq 1 20); do systemctl is-active --quiet {ShellQuote(serviceName)} && break; sleep 0.1; done",
                $"systemctl is-active --quiet {ShellQuote(serviceName)}",
                $"final_md5=$(md5sum {ShellQuote(remoteBinaryPath)} | awk '{{print $1}}')",
                "printf 'UPLOADED_MD5=%s\\nFINAL_MD5=%s\\n' \"$uploaded_md5\" \"$final_md5\"");
            var upgradeOutput = await RunSshAsync(BuildPrivilegedRemoteCommand(upgradeScript), cancellationToken, host);
            timing.Add($"remoteUpgrade={timer.ElapsedMilliseconds}ms");

            var uploadedMd5 = ParseTaggedMd5(upgradeOutput, "UPLOADED_MD5");
            var finalMd5 = ParseTaggedMd5(upgradeOutput, "FINAL_MD5");
            if (!string.Equals(uploadedMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                return new ApplicationUpgradeResult { Message = "Uploaded application MD5 verification failed." };
            }

            hasBackup = true;
            if (!string.Equals(finalMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Final application MD5 verification failed.");
            }

            timing.Add($"total={totalTimer.ElapsedMilliseconds}ms");
            return new ApplicationUpgradeResult
            {
                Success = true,
                FinalMd5 = finalMd5,
                Message = $"Application upgrade completed over SSH/SCP. Timing: {string.Join(", ", timing)}"
            };
        }
        catch (Exception ex)
        {
            try
            {
                var host = await ResolveTcpHostAsync(cancellationToken);
                await RunSshAsync(BuildPrivilegedRemoteCommand($"systemctl stop {ShellQuote(serviceName)}"), cancellationToken, host);
                if (hasBackup)
                {
                    await RunSshAsync(BuildPrivilegedRemoteCommand($"mv -f {ShellQuote(remoteBackupPath)} {ShellQuote(remoteBinaryPath)}"), cancellationToken, host);
                }
                await RunSshAsync(BuildPrivilegedRemoteCommand($"chmod 755 {ShellQuote(remoteBinaryPath)}"), cancellationToken, host);
                await RunSshAsync(BuildPrivilegedRemoteCommand($"systemctl start {ShellQuote(serviceName)}"), cancellationToken, host);
            }
            catch (Exception rollbackError)
            {
                return new ApplicationUpgradeResult { Message = $"Upgrade failed: {ex.Message}; rollback failed: {rollbackError.Message}; Timing: {string.Join(", ", timing)}, total={totalTimer.ElapsedMilliseconds}ms" };
            }

            return new ApplicationUpgradeResult { Message = $"Upgrade failed and was rolled back: {ex.Message}; Timing: {string.Join(", ", timing)}, total={totalTimer.ElapsedMilliseconds}ms" };
        }
    }

    private async Task<string> RunSshAsync(string remoteCommand, CancellationToken cancellationToken, string? host = null)
    {
        var resolvedHost = host ?? await ResolveTcpHostAsync(cancellationToken);
        return await Task.Run(() =>
        {
            using var client = CreateSshClient(resolvedHost);
            client.Connect();
            var command = client.RunCommand(remoteCommand);
            client.Disconnect();
            if (command.ExitStatus != 0)
            {
                throw new InvalidOperationException($"SSH command failed on {resolvedHost}: {command.Error.Trim()} {command.Result.Trim()}".Trim());
            }

            return command.Result;
        }, cancellationToken);
    }

    private async Task RunScpAsync(string localPath, string host, string remotePath, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var client = CreateScpClient(host);
            client.Connect();
            using var file = File.OpenRead(localPath);
            client.Upload(file, remotePath);
            client.Disconnect();
        }, cancellationToken);
    }

    private SshClient CreateSshClient(string host)
    {
        var client = new SshClient(CreateConnectionInfo(host));
        client.HostKeyReceived += (_, args) => args.CanTrust = true;
        return client;
    }

    private ScpClient CreateScpClient(string host)
    {
        var client = new ScpClient(CreateConnectionInfo(host));
        client.HostKeyReceived += (_, args) => args.CanTrust = true;
        return client;
    }

    private ConnectionInfo CreateConnectionInfo(string host)
    {
        if (string.IsNullOrWhiteSpace(_sshPassword))
        {
            throw new InvalidOperationException("SSH password is not configured. Set upgrade.sshPassword for TCP OTA.");
        }

        var authentication = new PasswordAuthenticationMethod(_sshUser, _sshPassword);
        return new ConnectionInfo(host, _sshPort, _sshUser, authentication)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private string BuildPrivilegedRemoteCommand(string script)
    {
        if (string.IsNullOrWhiteSpace(_sshPassword))
        {
            return $"sh -c {ShellQuote(script)}";
        }

        return $"printf '%s\\n' {ShellQuote(_sshPassword)} | sudo -S -p '' sh -c {ShellQuote(script)}";
    }

    private async Task<string> RunProcessAsync(string fileName, string arguments, string label, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{label} command failed: {stderr.Trim()} {stdout.Trim()}".Trim());
        }

        return stdout;
    }

    private async Task<bool> RemoteFileExistsAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await RunAdbAsync($"shell test -f {Quote(path)}", cancellationToken);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task WaitForServiceActiveAsync(string serviceName, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 2.0);
        Exception? lastError = null;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            try
            {
                await RunAdbAsync($"shell systemctl is-active --quiet {Quote(serviceName)}", cancellationToken);
                return;
            }
            catch (InvalidOperationException ex)
            {
                lastError = ex;
                await Task.Delay(100, cancellationToken);
            }
        }

        throw new TimeoutException($"Service did not become active within 2 seconds: {serviceName}", lastError);
    }

    private static async Task<string> CalculateMd5Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await MD5.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ParseMd5(string output)
    {
        var value = output
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(token => token.Length == 32 && token.All(Uri.IsHexDigit));
        if (value is null)
            throw new InvalidOperationException($"Unable to parse application MD5 from ADB output: {output.Trim()}");
        return value.ToLowerInvariant();
    }

    private static string ParseTaggedMd5(string output, string tag)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(item => item.TrimStart().StartsWith(tag + "=", StringComparison.OrdinalIgnoreCase));
        if (line is null)
            throw new InvalidOperationException($"Unable to parse {tag} from ADB output: {output.Trim()}");

        return ParseMd5(line[(line.IndexOf('=') + 1)..]);
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private bool ShouldUseSshScpUpgrade()
    {
        if (string.Equals(_upgradeTransport, "sshScp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_upgradeTransport, "scp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(_upgradeTransport, "adb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !_useAdbForward;
    }

    private async Task<string> ResolveTcpHostAsync(CancellationToken cancellationToken)
    {
        return _discoveryService is null
            ? _tcpHost
            : await _discoveryService.ResolveHostAsync(cancellationToken);
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\"'\"'")}'";

    public async IAsyncEnumerable<TestSessionEvent> RunSessionAsync(
        string sessionId,
        string sn,
        IReadOnlyList<TestPlanItem> testPlan,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var remainingTests = testPlan.ToArray();
        var reconnectAttempts = 0;
        var resumeCandidateTestId = string.Empty;
        var reconnectDelayMs = GetEthernetLedReconnectDelayMs(remainingTests);

        while (remainingTests.Length > 0)
        {
            Log?.Invoke($"PCBA session connect attempt: session={sessionId}, sn={sn}, tests={string.Join(",", remainingTests.Select(item => item.Id))}.");
            using var client = await ConnectPcbaAsync(cancellationToken);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            var command = new HostCommand
            {
                SessionId = sessionId,
                Sn = sn,
                CommandGroup = "session",
                Command = "start",
                Parameters = new Dictionary<string, object?> { ["tests"] = remainingTests }
            };

            var requestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, JsonOptions) + "\n");
            await stream.WriteAsync(requestBytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            _activeSessionStream = stream;
            _activeSessionId = sessionId;

            var lastRunningTestId = string.Empty;
            var lastEventTestId = string.Empty;
            var lastEventStatus = string.Empty;
            var shouldReconnect = false;
            var reconnectTransitionStartedAt = DateTimeOffset.UtcNow;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(cancellationToken);
                    }
                    catch (IOException)
                    {
                        line = null;
                    }
                    catch (InvalidOperationException)
                    {
                        line = null;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        var ethernetLedIndex = FindTestIndex(remainingTests, "ethernet_led");
                        var disconnectCanBeEthernetLedTransition = ethernetLedIndex >= 0 &&
                            (string.Equals(lastEventTestId, "ethernet_led", StringComparison.OrdinalIgnoreCase) ||
                             ethernetLedIndex > 0 &&
                             string.Equals(lastEventTestId, remainingTests[ethernetLedIndex - 1].Id, StringComparison.OrdinalIgnoreCase));
                        if (reconnectAttempts < 6 && (disconnectCanBeEthernetLedTransition || !string.IsNullOrWhiteSpace(lastRunningTestId) || !string.IsNullOrWhiteSpace(resumeCandidateTestId)))
                        {
                            reconnectTransitionStartedAt = DateTimeOffset.UtcNow;
                            Log?.Invoke($"ETHERNET_LED_RECONNECT_START utc={reconnectTransitionStartedAt:O} lastTest={lastEventTestId}.");
                            Log?.Invoke($"PCBA session stream closed after {lastEventTestId} ({lastEventStatus}); automatic resume will be attempted.");
                            shouldReconnect = true;
                            break;
                        }

                        Log?.Invoke($"PCBA session stream closed unexpectedly: lastTest={lastEventTestId}, lastStatus={lastEventStatus}.");
                        throw new InvalidOperationException("PCBA closed the test-session stream before completion.");
                    }

                    var testEvent = JsonSerializer.Deserialize<TestSessionEvent>(line, JsonOptions)
                        ?? throw new InvalidOperationException("Failed to parse PCBA test-session event.");
                    if (string.Equals(testEvent.TestId, "hdmi", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(testEvent.Event, "session.completed", StringComparison.OrdinalIgnoreCase))
                    {
                        Log?.Invoke($"PCBA RX: event={testEvent.Event}, test={testEvent.TestId}, status={testEvent.Status}, code={testEvent.ResultCode}, message={testEvent.Message}");
                    }
                    yield return testEvent;
                    lastEventTestId = testEvent.TestId;
                    lastEventStatus = testEvent.Status;

                    if (testEvent.Event == "test.report" &&
                        string.Equals(testEvent.Status, "running", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(testEvent.TestId))
                    {
                        lastRunningTestId = testEvent.TestId;
                        resumeCandidateTestId = testEvent.TestId;
                    }

                    if (testEvent.Status is "passed" or "failed" or "skipped" &&
                        string.Equals(testEvent.TestId, lastRunningTestId, StringComparison.OrdinalIgnoreCase))
                    {
                        lastRunningTestId = string.Empty;
                    }

                    if (testEvent.Event == "session.completed")
                    {
                        Log?.Invoke($"PCBA session completed event received: status={testEvent.Status}, code={testEvent.ResultCode}.");
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

            if (!shouldReconnect)
            {
                yield break;
            }

            var resumeTestId = string.Equals(lastEventTestId, "ethernet_led", StringComparison.OrdinalIgnoreCase)
                ? "ethernet_led"
                : !string.IsNullOrWhiteSpace(lastRunningTestId) ? lastRunningTestId : resumeCandidateTestId;
            var resumeIndex = FindTestIndex(remainingTests, resumeTestId);
            if (resumeIndex < 0)
            {
                throw new InvalidOperationException("Unable to resume Ethernet LED session after reconnect.");
            }
            remainingTests = remainingTests.Skip(resumeIndex).ToArray();
            resumeCandidateTestId = remainingTests[0].Id;
            if (string.Equals(remainingTests[0].Id, "ethernet_led", StringComparison.OrdinalIgnoreCase))
            {
                remainingTests[0] = WithParameter(remainingTests[0], "resumeAfterReconnect", true);
            }
            if (remainingTests.Length == 0)
            {
                yield break;
            }

            reconnectAttempts++;
            Log?.Invoke($"PCBA session reconnect scheduled: lastTest={lastEventTestId}, lastStatus={lastEventStatus}, resumeTests={string.Join(",", remainingTests.Select(item => item.Id))}, configuredDelayMs={reconnectDelayMs}; probing for service readiness.");
            var retryDelayMs = Math.Min(4000, 250 * (1 << Math.Min(reconnectAttempts - 1, 4)));
            var reconnectReadyAt = await WaitForPcbaReconnectAsync(
                string.Equals(remainingTests[0].Id, "ethernet_led", StringComparison.OrdinalIgnoreCase)
                    ? reconnectDelayMs
                    : retryDelayMs,
                cancellationToken);
            Log?.Invoke($"ETHERNET_LED_RECONNECT_READY utc={reconnectReadyAt:O} elapsedMs={(reconnectReadyAt - reconnectTransitionStartedAt).TotalMilliseconds:F0}.");
        }
    }

    private async Task<DateTimeOffset> WaitForPcbaReconnectAsync(int configuredDelayMs, CancellationToken cancellationToken)
    {
        // Do not open a probe connection here: the device treats every new
        // connection as an active session and would supersede the real resume.
        var graceMs = Math.Clamp(configuredDelayMs, 250, 1500);
        Log?.Invoke($"PCBA reconnect settle delay={graceMs}ms; opening one authoritative resume connection afterward.");
        await Task.Delay(graceMs, cancellationToken);
        return DateTimeOffset.UtcNow;
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
        Log?.Invoke($"PCBA HDMI decision preparing: session={sessionId}, test={testId}, passed={passed}, bytes={bytes.Length}, stream={_activeSessionStream.GetHashCode()}");
        await _sessionWriteGate.WaitAsync(cancellationToken);
        try
        {
            await _activeSessionStream.WriteAsync(bytes, cancellationToken);
            await _activeSessionStream.FlushAsync(cancellationToken);
            Log?.Invoke($"PCBA HDMI decision flushed: session={sessionId}, test={testId}, passed={passed}");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"PCBA HDMI decision write failed: session={sessionId}, test={testId}, type={ex.GetType().Name}, message={ex.Message}");
            throw;
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
            Version = response.Data.Version,
            TestItems = response.Data.TestItems
        };
    }

    public async Task<CommandResponse> SyncSessionSummaryAsync(
        string sessionId,
        string sn,
        string boardId,
        string finalVerdict,
        IReadOnlyList<TestResultRecord> testResults,
        CancellationToken cancellationToken = default)
    {
        var command = new HostCommand
        {
            SessionId = sessionId,
            Sn = sn,
            BoardId = boardId,
            CommandGroup = "sys",
            Command = "sync_session_summary",
            Parameters = new Dictionary<string, object?>
            {
                ["finalVerdict"] = finalVerdict,
                ["testResults"] = testResults.Select(item => new Dictionary<string, object?>
                {
                    ["testId"] = item.TestId,
                    ["status"] = item.Status
                }).ToArray()
            }
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
        using var client = await ConnectPcbaAsync(cancellationToken);

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
        if (!_useAdbForward)
        {
            return;
        }

        var arguments = BuildAdbArguments($"forward tcp:{_localPort} tcp:{_remotePort}");
        Log?.Invoke($"PCBA ADB forward start: adb={_adbPath}, args={arguments}.");

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
            Log?.Invoke($"PCBA ADB forward failed: exit={process.ExitCode}, stdout={stdOut.Trim()}, stderr={stdErr.Trim()}.");
            throw new InvalidOperationException(
                $"ADB forward failed with exit code {process.ExitCode}. stdout: {stdOut} stderr: {stdErr}");
        }
        Log?.Invoke($"PCBA ADB forward ok: localPort={_localPort}, remotePort={_remotePort}.");
    }

    private string BuildAdbArguments(string command)
    {
        return string.IsNullOrWhiteSpace(_deviceSerial)
            ? command
            : $"-s {_deviceSerial} {command}";
    }

    private async Task<TcpClient> ConnectPcbaAsync(CancellationToken cancellationToken)
    {
        await EnsureForwardAsync(cancellationToken);

        var host = _useAdbForward
            ? "127.0.0.1"
            : _discoveryService is null
                ? _tcpHost
                : await _discoveryService.ResolveHostAsync(cancellationToken);
        var port = _useAdbForward ? _localPort : _tcpPort;

        var client = new TcpClient();
        try
        {
            var requiredLocalAddress = !_useAdbForward ? _discoveryService?.ResolveLocalAddress() : null;
            if (requiredLocalAddress is not null)
            {
                client.Client.Bind(new IPEndPoint(requiredLocalAddress, 0));
            }
            Log?.Invoke($"PCBA TCP connect start: mode={(_useAdbForward ? "adb-forward" : "tcp")}, host={host}, port={port}.");
            await client.ConnectAsync(host, port, cancellationToken);
            if (requiredLocalAddress is not null &&
                client.Client.LocalEndPoint is IPEndPoint localEndPoint &&
                !requiredLocalAddress.Equals(localEndPoint.Address.MapToIPv4()))
            {
                throw new InvalidOperationException($"TCP 连接未使用指定以太网卡：expected={requiredLocalAddress}, actual={localEndPoint.Address}.");
            }
            Log?.Invoke($"PCBA TCP connect ok: host={host}, port={port}, local={client.Client.LocalEndPoint}.");
            return client;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"PCBA TCP connect failed: host={host}, port={port}, error={ex.GetType().Name}: {ex.Message}.");
            client.Dispose();
            throw;
        }
    }

    private static int FindTestIndex(IReadOnlyList<TestPlanItem> tests, string testId)
    {
        if (string.IsNullOrWhiteSpace(testId))
        {
            return -1;
        }

        for (var index = 0; index < tests.Count; index++)
        {
            if (string.Equals(tests[index].Id, testId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static TestPlanItem WithParameter(TestPlanItem item, string key, object? value)
    {
        var parameters = new Dictionary<string, object?>(item.Parameters, StringComparer.OrdinalIgnoreCase)
        {
            [key] = value
        };
        return new TestPlanItem
        {
            Id = item.Id,
            Skip = item.Skip,
            SkipReason = item.SkipReason,
            Parameters = parameters
        };
    }

    private static int GetEthernetLedReconnectDelayMs(IReadOnlyList<TestPlanItem> tests)
    {
        const int fallback = 8000;
        var ethernetLed = tests.FirstOrDefault(item => string.Equals(item.Id, "ethernet_led", StringComparison.OrdinalIgnoreCase));
        if (ethernetLed is null)
        {
            return fallback;
        }

        return TryGetIntParameter(ethernetLed.Parameters, "reconnectDelayMs", fallback);
    }

    private static int TryGetIntParameter(IReadOnlyDictionary<string, object?> parameters, string key, int fallback)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => longValue is >= int.MinValue and <= int.MaxValue ? (int)longValue : fallback,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            string text when int.TryParse(text, out var parsed) => parsed,
            JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsed) => parsed,
            _ => fallback
        };
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
        public IReadOnlyList<BoardTestItemSummary> TestItems { get; init; } = Array.Empty<BoardTestItemSummary>();
    }
}
