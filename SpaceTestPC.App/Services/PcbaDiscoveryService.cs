using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class PcbaDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex Ipv4Regex = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private readonly PcbaConnectionConfiguration _configuration;
    private string? _resolvedHost;
    private EthernetAdapterInfo? _selectedAdapter;
    public event Action<string>? Log;

    public PcbaDiscoveryService(PcbaConnectionConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<string> ResolveHostAsync(CancellationToken cancellationToken = default)
    {
        var adapter = ResolveSelectedAdapter();
        if (!string.IsNullOrWhiteSpace(_configuration.Host) &&
            !string.Equals(_configuration.Host, "auto", StringComparison.OrdinalIgnoreCase))
        {
            ValidateHostUsesSelectedAdapter(_configuration.Host.Trim(), adapter);
            Log?.Invoke($"PCBA discovery skipped: configured host={_configuration.Host.Trim()}.");
            return _configuration.Host.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_resolvedHost))
        {
            Log?.Invoke($"PCBA discovery cached: host={_resolvedHost}.");
            return _resolvedHost;
        }

        if (!_configuration.Discovery.Enabled)
        {
            throw new InvalidOperationException("PCBA TCP host is auto, but network discovery is disabled.");
        }

        var candidates = GetCandidateAddresses().Distinct().ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException("No candidate IP addresses are available for PCBA discovery.");
        }

        Log?.Invoke($"PCBA discovery started: port={_configuration.Port}, candidates={candidates.Length}, parallel={Math.Max(1, _configuration.Discovery.MaxParallel)}, tcpTimeoutMs={Math.Max(100, _configuration.Discovery.ConnectTimeoutMs)}.");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var gate = new SemaphoreSlim(Math.Max(1, _configuration.Discovery.MaxParallel));
        var tasks = candidates.Select(candidate => ProbeCandidateAsync(candidate, gate, linkedCancellation.Token)).ToArray();

        while (tasks.Length > 0)
        {
            var completed = await Task.WhenAny(tasks);
            var result = await completed;
            if (!string.IsNullOrWhiteSpace(result))
            {
                await linkedCancellation.CancelAsync();
                try
                {
                    await Task.WhenAll(tasks.Where(task => !ReferenceEquals(task, completed)));
                }
                catch (OperationCanceledException)
                {
                }
                _resolvedHost = result;
                Log?.Invoke($"PCBA discovery verified: host={result}.");
                return result;
            }

            tasks = tasks.Where(task => !ReferenceEquals(task, completed)).ToArray();
        }

        throw new InvalidOperationException($"No PCBA service was discovered on TCP port {_configuration.Port} after probing {candidates.Length} candidate IPs.");
    }

    private async Task<string?> ProbeCandidateAsync(string host, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var pingReachable = ResolveSelectedAdapter() is null && await IsPingReachableAsync(host, cancellationToken);

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(Math.Max(100, _configuration.Discovery.ConnectTimeoutMs));
            using var client = new TcpClient();
            if (ResolveSelectedAdapter() is { } adapter)
            {
                client.Client.Bind(new IPEndPoint(adapter.Address, 0));
            }
            await client.ConnectAsync(host, _configuration.Port, connectTimeout.Token);
            var pingStatus = ResolveSelectedAdapter() is null ? (pingReachable ? "ok" : "no_reply") : "disabled_ethernet_only";
            Log?.Invoke($"PCBA discovery candidate: host={host}, local={client.Client.LocalEndPoint}, ping={pingStatus}, tcp=connected.");

            await using var stream = client.GetStream();
            var command = new HostCommand
            {
                SessionId = "discovery",
                CommandGroup = "sys",
                Command = "get_version"
            };

            var request = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command, JsonOptions) + "\n");
            await stream.WriteAsync(request, connectTimeout.Token);
            await stream.FlushAsync(connectTimeout.Token);

            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(connectTimeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("resultCode", out var resultCode) || resultCode.GetInt32() != 0)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("data", out var data))
            {
                return null;
            }

            if (data.TryGetProperty("appName", out var appName) &&
                string.Equals(appName.GetString(), "spacetest3576", StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke($"PCBA discovery response: host={host}, app=spacetest3576.");
                return host;
            }

            if (data.TryGetProperty("path", out var path) &&
                path.GetString()?.Contains("spacetest3576", StringComparison.OrdinalIgnoreCase) == true)
            {
                Log?.Invoke($"PCBA discovery response: host={host}, path={path.GetString()}.");
                return host;
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> IsPingReachableAsync(string host, CancellationToken cancellationToken)
    {
        var timeoutMs = Math.Max(50, _configuration.Discovery.PingTimeoutMs);
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs).WaitAsync(cancellationToken);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            // Some devices block ICMP. TCP probing still gives a reliable final answer.
            return false;
        }
    }

    private IEnumerable<string> GetCandidateAddresses()
    {
        var adapter = ResolveSelectedAdapter();
        foreach (var address in GetArpCandidateAddresses(adapter))
        {
            yield return address;
        }

        if (adapter is not null)
        {
            Log?.Invoke($"PCBA discovery restricted to Ethernet adapter: name={adapter.Name}, id={adapter.Id}, localIp={adapter.Address}, subnet={adapter.Cidr}.");
            foreach (var address in EnumerateCidr(adapter.Cidr))
            {
                if (!address.Equals(adapter.Address.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    yield return address;
                }
            }

            yield break;
        }

        if (!string.IsNullOrWhiteSpace(_configuration.Discovery.StartIp) &&
            !string.IsNullOrWhiteSpace(_configuration.Discovery.EndIp) &&
            IPAddress.TryParse(_configuration.Discovery.StartIp, out var start) &&
            IPAddress.TryParse(_configuration.Discovery.EndIp, out var end))
        {
            Log?.Invoke($"PCBA discovery range: {_configuration.Discovery.StartIp}-{_configuration.Discovery.EndIp}.");
            foreach (var address in EnumerateRange(start, end))
            {
                yield return address;
            }

            yield break;
        }

        if (!string.IsNullOrWhiteSpace(_configuration.Discovery.Subnet) &&
            !string.Equals(_configuration.Discovery.Subnet, "auto", StringComparison.OrdinalIgnoreCase))
        {
            Log?.Invoke($"PCBA discovery subnet: {_configuration.Discovery.Subnet}.");
            foreach (var address in EnumerateCidr(_configuration.Discovery.Subnet))
            {
                yield return address;
            }

            yield break;
        }

        Log?.Invoke("PCBA discovery subnet: auto from local IPv4 adapters.");
        foreach (var address in EnumerateLocalSubnets(Log))
        {
            yield return address;
        }
    }

    private IEnumerable<string> GetArpCandidateAddresses(EthernetAdapterInfo? selectedAdapter)
    {
        var localSubnets = selectedAdapter is null ? GetLocalIpv4Subnets().ToArray() : [];
        if (selectedAdapter is null && localSubnets.Length == 0)
        {
            yield break;
        }

        string output;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "arp",
                    Arguments = "-a",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }
                Log?.Invoke("PCBA discovery ARP candidates skipped: arp -a timed out.");
                yield break;
            }

            if (process.ExitCode != 0)
            {
                Log?.Invoke("PCBA discovery ARP candidates skipped: arp -a failed.");
                yield break;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"PCBA discovery ARP candidates skipped: {ex.Message}");
            yield break;
        }

        var candidates = Ipv4Regex.Matches(output)
            .Select(match => match.Value)
            .Where(value => IPAddress.TryParse(value, out var address) &&
                            (selectedAdapter?.Contains(address) ?? IsInAnyLocalSubnet(address, localSubnets)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length > 0)
        {
            Log?.Invoke($"PCBA discovery ARP candidates: {string.Join(",", candidates)}.");
        }

        foreach (var candidate in candidates)
        {
            yield return candidate;
        }
    }

    public IPAddress? ResolveLocalAddress() => ResolveSelectedAdapter()?.Address;

    private EthernetAdapterInfo? ResolveSelectedAdapter()
    {
        if (!_configuration.EthernetOnly)
        {
            return null;
        }

        _selectedAdapter ??= EthernetAdapterService.Resolve(_configuration);
        return _selectedAdapter;
    }

    private static void ValidateHostUsesSelectedAdapter(string host, EthernetAdapterInfo? adapter)
    {
        if (adapter is null)
        {
            return;
        }

        if (!IPAddress.TryParse(host, out var address) || !adapter.Contains(address))
        {
            throw new InvalidOperationException($"设备地址 {host} 不在指定以太网卡网段 {adapter.Cidr} 内，已拒绝通过其他网卡通信。");
        }
    }

    private static IEnumerable<(uint Network, uint Mask)> GetLocalIpv4Subnets()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                var mask = ToUInt32(unicast.IPv4Mask);
                yield return (ToUInt32(unicast.Address) & mask, mask);
            }
        }
    }

    private static bool IsInAnyLocalSubnet(IPAddress address, IReadOnlyList<(uint Network, uint Mask)> localSubnets)
    {
        var value = ToUInt32(address);
        return localSubnets.Any(subnet => (value & subnet.Mask) == subnet.Network);
    }

    private static IEnumerable<string> EnumerateLocalSubnets(Action<string>? log)
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                {
                    continue;
                }

                var start = ToUInt32(unicast.Address) & ToUInt32(unicast.IPv4Mask);
                var end = start | ~ToUInt32(unicast.IPv4Mask);
                log?.Invoke($"PCBA discovery adapter: {networkInterface.Name}, address={unicast.Address}, mask={unicast.IPv4Mask}, range={FromUInt32(start + 1)}-{FromUInt32(end - 1)}.");
                for (var value = start + 1; value < end; value++)
                {
                    var candidate = FromUInt32(value);
                    if (!candidate.Equals(unicast.Address))
                    {
                        yield return candidate.ToString();
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateCidr(string cidr)
    {
        var parts = cidr.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefix))
        {
            yield break;
        }

        if (prefix is < 1 or > 30)
        {
            yield break;
        }

        var mask = uint.MaxValue << (32 - prefix);
        var start = ToUInt32(network) & mask;
        var end = start | ~mask;
        for (var value = start + 1; value < end; value++)
        {
            yield return FromUInt32(value).ToString();
        }
    }

    private static IEnumerable<string> EnumerateRange(IPAddress start, IPAddress end)
    {
        var startValue = ToUInt32(start);
        var endValue = ToUInt32(end);
        if (endValue < startValue)
        {
            yield break;
        }

        for (var value = startValue; value <= endValue; value++)
        {
            yield return FromUInt32(value).ToString();
        }
    }

    private static uint ToUInt32(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    private static IPAddress FromUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
