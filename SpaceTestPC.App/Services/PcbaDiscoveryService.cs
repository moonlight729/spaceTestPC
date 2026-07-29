using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App.Services;

public sealed class PcbaDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PcbaConnectionConfiguration _configuration;
    private string? _resolvedHost;

    public PcbaDiscoveryService(PcbaConnectionConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<string> ResolveHostAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_configuration.Host) &&
            !string.Equals(_configuration.Host, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return _configuration.Host.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_resolvedHost))
        {
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
                _resolvedHost = result;
                return result;
            }

            tasks = tasks.Where(task => !ReferenceEquals(task, completed)).ToArray();
        }

        throw new InvalidOperationException($"No PCBA service was discovered on TCP port {_configuration.Port}.");
    }

    private async Task<string?> ProbeCandidateAsync(string host, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!await IsPingReachableAsync(host, cancellationToken))
            {
                return null;
            }

            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(Math.Max(100, _configuration.Discovery.ConnectTimeoutMs));
            using var client = new TcpClient();
            await client.ConnectAsync(host, _configuration.Port, connectTimeout.Token);

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
                return host;
            }

            if (data.TryGetProperty("path", out var path) &&
                path.GetString()?.Contains("spacetest3576", StringComparison.OrdinalIgnoreCase) == true)
            {
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
            return true;
        }
    }

    private IEnumerable<string> GetCandidateAddresses()
    {
        if (!string.IsNullOrWhiteSpace(_configuration.Discovery.StartIp) &&
            !string.IsNullOrWhiteSpace(_configuration.Discovery.EndIp) &&
            IPAddress.TryParse(_configuration.Discovery.StartIp, out var start) &&
            IPAddress.TryParse(_configuration.Discovery.EndIp, out var end))
        {
            foreach (var address in EnumerateRange(start, end))
            {
                yield return address;
            }

            yield break;
        }

        if (!string.IsNullOrWhiteSpace(_configuration.Discovery.Subnet) &&
            !string.Equals(_configuration.Discovery.Subnet, "auto", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var address in EnumerateCidr(_configuration.Discovery.Subnet))
            {
                yield return address;
            }

            yield break;
        }

        foreach (var address in EnumerateLocalSubnets())
        {
            yield return address;
        }
    }

    private static IEnumerable<string> EnumerateLocalSubnets()
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
