using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetOps.Application;
using NetOps.Domain;

namespace NetOps.Infrastructure;

public sealed record ProbeResult(bool Responded, bool Reachable, MetricReading[] Metrics, TopologyLink[] Links,
    string? Error = null, double? LatencyMs = null, long? Uptime = null, bool TopologyCollected = false, string? SystemName = null, string? ChassisIdentity = null, StackMemberState[]? Members = null);
public interface IMonitorProbe { Task<ProbeResult> ReadAsync(Device device, MonitorProfile profile, bool topology, CancellationToken ct); }
public sealed class MonitorProbe(ISecretVault vault, ISessionFactory sessions) : IMonitorProbe
{
    public async Task<ProbeResult> ReadAsync(Device device, MonitorProfile profile, bool topology, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow; var stopwatch = Stopwatch.StartNew(); bool responded = false;
        if (device.IsSimulation) return Demo(device, now, topology);
        try
        {
            if (profile.Protocol == MonitorProtocol.Ssh) return await SshAsync(device, profile, topology, ct);
            if (profile.CredentialReference is null) throw new ArgumentException("尚未配置监控凭据。");
            var credential = JsonSerializer.Deserialize<MonitorCredential>(vault.Read(profile.CredentialReference))!;
            var source = new SnmpSource(device, profile, credential); List<SnmpCell> cells = [];
            cells.AddRange(await source.GetAsync(["1.3.6.1.2.1.1.3.0", "1.3.6.1.2.1.1.5.0", "1.0.8802.1.1.2.1.3.2.0"], ct)); responded = true;
            var latency = stopwatch.Elapsed.TotalMilliseconds;
            foreach (var oid in new[] { profile.CpuOid, profile.MemoryOid, profile.TemperatureOid }.Where(o => o.Length > 0)) cells.AddRange(await source.GetAsync([oid], ct));
            foreach (var root in new[] { "1.3.6.1.2.1.25.3.3.1.2", "1.3.6.1.2.1.25.2.3.1", "1.3.6.1.2.1.99.1.1.1", "1.3.6.1.2.1.47.1.1.1.1.7" }) cells.AddRange(await source.WalkAsync(root, ct));
            bool neighborsComplete = false; List<TopologyLink> neighbors = []; string? warning = null;
            if (topology)
            {
                try { cells.AddRange(await source.WalkAsync("1.0.8802.1.1.2.1.3.7.1", ct)); cells.AddRange(await source.WalkAsync("1.0.8802.1.1.2.1.4.1.1", ct)); neighbors.AddRange(TopologyDiscovery.FromSnmp(device.Id, cells, now)); neighborsComplete = true; }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { warning = "指标已采集，LLDP 邻居本轮未采集成功。"; }
            }
            var chassis = cells.FirstOrDefault(c => c.Oid == "1.0.8802.1.1.2.1.3.2.0");
            var identity = chassis?.Bytes is { Length: 6 } bytes ? string.Join(":", bytes.Select(b => b.ToString("x2"))) : chassis?.Text;
            return new(true, true, MetricParser.Parse(cells, profile, now), neighbors.ToArray(), warning, latency, (long?)(cells.FirstOrDefault(c => c.Oid == "1.3.6.1.2.1.1.3.0")?.Number / 100), neighborsComplete, cells.FirstOrDefault(c => c.Oid == "1.3.6.1.2.1.1.5.0")?.Text, identity);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var reachable = responded || ex is InvalidOperationException && ex.Message.StartsWith("设备已响应") || await PingAsync(device.ManagementIp, ct);
            var error = ex is ArgumentException ? ex.Message : ex is InvalidOperationException && ex.Message.StartsWith("设备已响应") ? ex.Message : responded ? "设备已响应，部分指标采集失败。" : "监控未取得有效响应，请检查网络、凭据和协议权限。";
            return new(false, reachable, Failed(now), [], error);
        }
    }
    private static async Task<bool> PingAsync(string address, CancellationToken ct)
    { try { using var ping = new Ping(); return (await ping.SendPingAsync(address, 1500).WaitAsync(ct)).Status == IPStatus.Success; } catch (Exception) when (!ct.IsCancellationRequested) { return false; } }
    private async Task<ProbeResult> SshAsync(Device device, MonitorProfile profile, bool topology, CancellationToken ct)
    {
        var connection = device.GetConnections().FirstOrDefault(p => p.Protocol == ConnectionProtocol.Ssh && p.Id == device.PreferredConnectionId) ?? device.GetConnections().FirstOrDefault(p => p.Protocol == ConnectionProtocol.Ssh) ?? throw new ArgumentException("设备没有可用的 SSH 连接配置。");
        await using var cli = new CliConversation(sessions.Create(ConnectionProtocol.Ssh)); await cli.OpenAsync(device, connection, ct); var now = DateTimeOffset.UtcNow;
        var commands = device.Vendor switch
        {
            Vendor.Huawei => new[] { "display cpu-usage", "display memory-usage", "display temperature" },
            Vendor.H3C => ["display cpu-usage", "display memory", "display environment"],
            Vendor.Cisco => ["show processes cpu", "show processes memory", "show environment temperature status"],
            Vendor.Ruijie => ["show cpu", "show memory", "show temperature"], _ => throw new NotSupportedException("此品牌请使用 SNMP 或厂商指标 OID 监控。")
        };
        var outputs = new string[3]; string? warning = null;
        for (var i = 0; i < 3; i++) { try { outputs[i] = await cli.ExecuteAsync(commands[i], ct); } catch (InvalidOperationException) { outputs[i] = ""; warning = "部分监控命令不支持或权限不足。"; } }
        var metrics = SshMetrics.Parse(outputs[0], outputs[1], outputs[2], profile, now); List<TopologyLink> links = []; bool complete = false; StackMemberState[]? members = null;
        if (topology)
        {
            try { var observed = SwitchInventory.Members(device.Vendor, await cli.ExecuteAsync(SwitchInventory.StackCommand(device.Vendor), ct)); if (observed.Length > 0) members = observed; }
            catch (InvalidOperationException) { }
            try { var raw = await cli.ExecuteAsync(device.Vendor == Vendor.Huawei ? "display lldp neighbor" : device.Vendor == Vendor.H3C ? "display lldp neighbor-information verbose" : "show lldp neighbors detail", ct); links.AddRange(TopologyDiscovery.FromCli(device.Id, raw, now)); complete = true; }
            catch (InvalidOperationException) { warning = "指标已读取，LLDP 邻居未采集。"; }
            if (device.Vendor == Vendor.Cisco && links.Count == 0)
            { try { links.AddRange(TopologyDiscovery.FromCli(device.Id, await cli.ExecuteAsync("show cdp neighbors detail", ct), now, "CDP / SSH")); complete = true; } catch (InvalidOperationException) { } }
        }
        return new(true, true, metrics, links.ToArray(), warning, TopologyCollected: complete, Members: members);
    }
    private static MetricReading[] Failed(DateTimeOffset now) => new[] { ("cpu", "CPU", "%"), ("memory", "内存", "%"), ("temperature", "温度", "℃") }.Select(m => new MetricReading(m.Item1, m.Item2, null, m.Item3, CollectionQuality.Failed, now, "采集器")).ToArray();
    public static ProbeResult Demo(Device device, DateTimeOffset now, bool topology)
    {
        var offset = Math.Abs(device.Id.GetHashCode() % 12); var phase = now.ToUnixTimeSeconds() / 60.0;
        int?[] members = device.Model.Contains("双成员") ? [1, 2] : [null]; List<MetricReading> metrics = [];
        foreach (var member in members) { var suffix = member?.ToString() ?? "system"; var prefix = member is null ? "设备" : $"成员 {member}"; metrics.Add(new("cpu." + suffix, prefix + " CPU", Math.Round(22 + offset + Math.Sin(phase) * 7, 1), "%", CollectionQuality.Valid, now, "模拟数据", member)); metrics.Add(new("memory." + suffix, prefix + " 内存", 38 + offset, "%", CollectionQuality.Valid, now, "模拟数据", member)); metrics.Add(new("temperature." + suffix, prefix + " 主板温度", Math.Round(36 + offset / 2.0 + Math.Sin(phase) * 2, 1), "℃", CollectionQuality.Valid, now, "模拟数据", member)); }
        return new(true, true, metrics.ToArray(), [], LatencyMs: 1.5, Uptime: 86400 * 7, TopologyCollected: topology, SystemName: device.Name, Members: device.Model.Contains("双成员") ? [new(1,"Active","模拟在线",PortCount:24,Online:true), new(2,"Standby","模拟在线",PortCount:48,Online:true)] : null);
    }
}
public static class SshMetrics
{
    public static MetricReading[] Parse(string cpu, string memory, string temperature, MonitorProfile profile, DateTimeOffset now)
    {
        double? Match(string text, params string[] patterns) { foreach (var pattern in patterns) { var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase); if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) return value; } return null; }
        var c = Match(cpu, @"one minute\s*:\s*([\d.]+)%", @"([\d.]+)%\s+in last 1 minute", @"CPU (?:usage|utilization)(?: rate)?\s*[:=]\s*([\d.]+)%", @"CPU utilization for five seconds:\s*([\d.]+)%");
        var m = Match(memory, @"Memory (?:utilization|usage)(?: rate)?\s*(?::|=|is)\s*([\d.]+)%", @"Memory Using Percentage\s*(?:Is)?\s*[:=]?\s*([\d.]+)%");
        if (m is null) { var pool = Regex.Match(memory, @"Processor Pool Total:\s*(\d+)\s+Used:\s*(\d+)", RegexOptions.IgnoreCase); if (pool.Success && double.TryParse(pool.Groups[1].Value, out var total) && double.TryParse(pool.Groups[2].Value, out var used) && total > 0 && used <= total) m = used / total * 100; }
        if (m is null) { var pool = Regex.Match(memory, @"(?im)^\s*Mem:\s*(\d+)\s+(\d+)"); if (pool.Success && double.TryParse(pool.Groups[1].Value, out var total) && double.TryParse(pool.Groups[2].Value, out var used) && total > 0 && used <= total) m = used / total * 100; }
        var t = Match(temperature, @"Temperature(?: Value)?(?: is)?\s*[:=]?\s*(-?[\d.]+)\s*(?:Degree|C|℃)");
        MetricReading Metric(string key, string label, double? value, string unit) { var valid = value is double v && (unit == "%" ? v is >= 0 and <= 100 : v is >= -100 and <= 250); return new(key, label, valid ? value : null, unit, valid ? CollectionQuality.Valid : CollectionQuality.Unsupported, now, "SSH 只读解析", profile.MetricMemberId); }
        return [Metric("cpu.ssh", "CPU（设备报告口径）", c, "%"), Metric("memory.ssh", "内存使用率", m, "%"), Metric("temperature.ssh", "设备报告温度", t, "℃")];
    }
}
