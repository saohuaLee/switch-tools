using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NetOps.Infrastructure;

public sealed record DiscoveredEndpoint(string Address, int Port);
public static class DeviceDiscovery
{
    public static string[] Addresses(string start, string end)
    {
        uint Number(string text) { if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("发现范围请输入有效的 IPv4 起止地址。"); var b = ip.GetAddressBytes(); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]; }
        var first = Number(start); var last = Number(end);
        if (last < first || (ulong)last - first >= 256) throw new ArgumentException("发现范围必须从小到大，每次最多 256 个 IPv4 地址。");
        return Enumerable.Range(0, (int)(last - first + 1)).Select(i => { var n = first + (uint)i; return new IPAddress(new byte[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n }).ToString(); }).ToArray();
    }
    public static async Task<DiscoveredEndpoint[]> ScanAsync(string start, string end, int port, IProgress<int>? progress, CancellationToken ct)
    {
        if (port is < 1 or > 65535) throw new ArgumentException("发现端口必须为 1–65535。");
        var addresses = Addresses(start, end); var found = new ConcurrentBag<DiscoveredEndpoint>(); var completed = 0;
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (address, token) =>
        {
            using var client = new TcpClient(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
            try { await client.ConnectAsync(address, port, timeout.Token); found.Add(new(address, port)); }
            catch (SocketException) { } catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            progress?.Report(Interlocked.Increment(ref completed));
        });
        return found.OrderBy(e => Array.IndexOf(addresses, e.Address)).ToArray();
    }
}
