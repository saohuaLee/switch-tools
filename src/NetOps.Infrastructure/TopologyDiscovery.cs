using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NetOps.Domain;

namespace NetOps.Infrastructure;

public static class TopologyDiscovery
{
    public static Guid StableId(Guid deviceId, string localPort, string identity, string remotePort, string source) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceId}|{localPort}|{identity}|{remotePort}|{source}"))[..16]);
    public static string Identity(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]", "");
    public static Guid? Resolve(string identity, string name, IReadOnlyList<Device> devices, IReadOnlyList<MonitorSample> samples)
    {
        var matches = devices.Where(d => !d.Archived && (d.ManagementIp == identity || d.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || samples.Any(s => s.DeviceId == d.Id && ((s.ChassisIdentity is { Length: > 0 } c && Identity(c) == Identity(identity)) || s.SystemName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)))).Select(d => d.Id).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public static TopologyLink[] FromSnmp(Guid deviceId, IReadOnlyList<SnmpCell> cells, DateTimeOffset now)
    {
        const string remote = "1.0.8802.1.1.2.1.4.1.1."; List<TopologyLink> result = [];
        foreach (var item in cells.Where(c => c.Oid.StartsWith(remote + "5.")))
        {
            var index = item.Oid[(remote.Length + 2)..]; var parts = index.Split('.'); if (parts.Length != 3) continue;
            string Text(int column) => cells.FirstOrDefault(c => c.Oid == remote + column + "." + index)?.Text.Trim() ?? "";
            var localPort = cells.FirstOrDefault(c => c.Oid == "1.0.8802.1.1.2.1.3.7.1.3." + parts[1])?.Text;
            if (string.IsNullOrWhiteSpace(localPort)) localPort = cells.FirstOrDefault(c => c.Oid == "1.0.8802.1.1.2.1.3.7.1.4." + parts[1])?.Text;
            // lldpLocalPortNum 不是 ifIndex，缺少本地端口映射时明确保留原编号。
            localPort = string.IsNullOrWhiteSpace(localPort) ? "LLDP 本地端口 " + parts[1] : localPort;
            var identity = cells.FirstOrDefault(c => c.Oid == remote + "4." + index)?.Number == 4 && item.Bytes is { Length: 6 } bytes ? string.Join(":", bytes.Select(b => b.ToString("x2"))) : item.Text;
            var remotePort = Text(7); if (remotePort.Length == 0) remotePort = Text(8);
            var name = Text(9); if (name.Length == 0) name = identity;
            if (identity.Length == 0 || remotePort.Length == 0) continue;
            result.Add(new(StableId(deviceId, localPort, identity, remotePort, "LLDP"), deviceId, localPort, null, identity, name[..Math.Min(100, name.Length)], remotePort[..Math.Min(100, remotePort.Length)], "LLDP / SNMP", now));
        }
        return result.ToArray();
    }
    public static TopologyLink[] FromCli(Guid deviceId, string output, DateTimeOffset now, string source = "LLDP / SSH")
    {
        if (source.StartsWith("CDP"))
        {
            List<TopologyLink> cdp = [];
            foreach (var block in Regex.Split(output, @"(?im)(?=^\s*Device ID:)"))
            {
                var nameMatch = Regex.Match(block, @"(?im)^\s*Device ID:\s*(.+)$");
                var portMatch = Regex.Match(block, @"(?im)^\s*Interface:\s*([^,]+),\s*Port ID \(outgoing port\):\s*(.+)$");
                if (!nameMatch.Success || !portMatch.Success) continue;
                var peer = nameMatch.Groups[1].Value.Trim(); var localName = portMatch.Groups[1].Value.Trim(); var remoteName = portMatch.Groups[2].Value.Trim();
                cdp.Add(new(StableId(deviceId, localName, peer, remoteName, source), deviceId, localName, null, peer, peer, remoteName, source, now));
            }
            return cdp.ToArray();
        }
        List<TopologyLink> result = []; string local = "", chassis = "", name = "", remote = "";
        void Flush()
        {
            if (local.Length == 0 || remote.Length == 0 || chassis.Length == 0 && name.Length == 0) return;
            var identity = chassis.Length > 0 ? chassis : name;
            result.Add(new(StableId(deviceId, local, identity, remote, source), deviceId, local, null, identity, name.Length > 0 ? name : identity, remote, source, now));
        }
        foreach (var raw in (output + "\nEND-NEIGHBORS").Split('\n'))
        {
            var line = raw.Trim();
            var huawei = Regex.Match(line, @"^([A-Za-z][A-Za-z-]*[0-9/]+)\s+has\s+\d+\s+neighbor", RegexOptions.IgnoreCase);
            var m = Regex.Match(line, @"^(?:Local (?:Intf|Interface|Port)|(?:LLDP neighbor-information of port)|Interface)\s*[:：]?\s*(.+)$", RegexOptions.IgnoreCase);
            if (huawei.Success) { Flush(); local = huawei.Groups[1].Value; chassis = name = remote = ""; continue; }
            if (m.Success)
            {
                Flush(); local = m.Groups[1].Value.Trim().Trim(':'); chassis = name = remote = "";
                var bracket = Regex.Match(local, @"\[([^\]]+)\]"); if (bracket.Success) local = bracket.Groups[1].Value;
                var cdp = Regex.Match(local, @"^([^,]+),\s*Port ID \(outgoing port\):\s*(.+)$", RegexOptions.IgnoreCase); if (cdp.Success) { local = cdp.Groups[1].Value; remote = cdp.Groups[2].Value; }
            }
            else if (Regex.Match(line, @"^Chassis\s*(?:ID|Id)\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase) is { Success: true } id) chassis = id.Groups[1].Value.Trim();
            else if (Regex.Match(line, @"^(?:System (?:Name|name)|Device ID)\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase) is { Success: true } n) name = n.Groups[1].Value.Trim();
            else if (Regex.Match(line, @"^Port\s*(?:ID|Id)\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase) is { Success: true } p) remote = p.Groups[1].Value.Trim();
            if (line == "END-NEIGHBORS") Flush();
        }
        return result.DistinctBy(e => e.Id).ToArray();
    }
    public static TopologyLink[] Edges(IEnumerable<TopologyLink> links)
        => links.GroupBy(l => l.RemoteDeviceId is Guid remote ? string.Join("|", new[] { l.LocalDeviceId + "/" + SwitchInventory.NormalizePort(l.LocalPort), remote + "/" + SwitchInventory.NormalizePort(l.RemotePort) }.Order()) : l.Id.ToString()).Select(g => g.OrderByDescending(l => l.ObservedAt).First()).ToArray();
}
