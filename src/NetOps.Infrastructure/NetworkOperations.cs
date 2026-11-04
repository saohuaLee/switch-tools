using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using NetOps.Application;
using NetOps.Domain;
using Renci.SshNet;

namespace NetOps.Infrastructure;

public sealed partial class CliConversation(IDeviceSession session) : IAsyncDisposable
{
    public async Task OpenAsync(Device device, ConnectionProfile profile, CancellationToken ct)
    {
        await session.ConnectAsync(device, profile, ct);
        await ReceiveAsync(ct);
    }
    public async Task<string> ExecuteAsync(string command, CancellationToken ct)
    {
        await session.SendAsync(command + "\r\n", ct);
        var result = await ReceiveAsync(ct);
        if (CommandError().IsMatch(result)) throw new InvalidOperationException("设备拒绝命令，已停止后续步骤。请检查权限、系统版本及当前配置。");
        return result;
    }
    private async Task<string> ReceiveAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var buffer = new StringBuilder(); int pagerHandled = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!session.IsConnected) throw new IOException("会话已断开，操作未完成。");
            var part = await session.ReadAvailableAsync(token);
            if (part.Length > 0)
            {
                buffer.Append(part);
                if (buffer.Length > 2_000_000) throw new InvalidOperationException("设备响应超过 2 MB，已停止读取。");
                var text = Clean(buffer.ToString());
                var count = Pager().Matches(buffer.ToString()).Count;
                if (count > pagerHandled) { pagerHandled = count; await session.SendAsync(" ", token); }
                var last = text.TrimEnd().Split('\n').Last().Trim();
                if (Prompt().IsMatch(last)) return Pager().Replace(text, "");
            }
            await Task.Delay(30, token);
        }
    }
    public static string Clean(string text)
    {
        var clean = Ansi().Replace(text, "").Replace("\r", "");
        var result = new StringBuilder();
        foreach (var c in clean) { if (c == '\b') { if (result.Length > 0) result.Length--; } else if (c == '\n' || c == '\t' || c >= ' ') result.Append(c); }
        return result.ToString();
    }
    public ValueTask DisposeAsync() => session.DisposeAsync();
    [GeneratedRegex(@"^(?:<[\w.-]+>|\[[\w./~*() -]+\]|[\w.-]+(?:\([^\r\n)]*\))?[>#])\s*$")] private static partial Regex Prompt();
    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))")] private static partial Regex Ansi();
    [GeneratedRegex(@"--+\s*More\s*--+", RegexOptions.IgnoreCase)] private static partial Regex Pager();
    [GeneratedRegex(@"(?im)^\s*(?:%\s*(?:Invalid|Error|Unknown|Incomplete|Ambiguous|Permission)|Error:|Unrecognized command|.*(?:permission denied|insufficient privilege))")] private static partial Regex CommandError();
}

public static partial class ConfigurationParser
{
    public static DeviceState Parse(Device device, string raw, int revision = 1, bool allowEmpty = false)
    {
        if (device.Vendor is not (Vendor.Huawei or Vendor.H3C or Vendor.Cisco or Vendor.Ruijie)) throw new NotSupportedException("此厂商尚未提供结构化配置解析，可使用高级终端。");
        Dictionary<int, VlanState> vlans = new() { [1] = new(1, "VLAN1") };
        Dictionary<string, PortState> ports = new(StringComparer.OrdinalIgnoreCase);
        List<MacBinding> macs = []; List<(string Address, int? Vlan, string Port)> secure = [];
        Dictionary<string, PortSecurityState> security = new(StringComparer.OrdinalIgnoreCase);
        string? currentPort = null; int? currentVlan = null;
        foreach (var item in CliConversation.Clean(raw).Split('\n'))
        {
            var line = item.Trim();
            if (line is "#" or "!" or "return" or "end" or "exit" or "quit") { currentPort = null; currentVlan = null; continue; }
            if (line.StartsWith("interface ", StringComparison.OrdinalIgnoreCase))
            {
                currentVlan = null; var name = line[10..].Trim().Replace(" ", ""); currentPort = PhysicalPort().IsMatch(name) ? name : null;
                if (currentPort is not null) ports[currentPort] = new(currentPort, "", true, false, 0, PortMode.Unknown, 1, [], Extensions: new() { ["source"] = "running-config" });
                continue;
            }
            if (StaticMacVrp().Match(line) is { Success: true } vrp)
            { macs.Add(new(MacRules.Normalize(vrp.Groups[1].Value), int.Parse(vrp.Groups[3].Value), vrp.Groups[2].Value.Replace(" ", ""))); continue; }
            if (StaticMacIos().Match(line) is { Success: true } ios)
            { macs.Add(new(MacRules.Normalize(ios.Groups[1].Value), int.Parse(ios.Groups[2].Value), ios.Groups[3].Value.Replace(" ", ""))); continue; }
            if (currentPort is not null)
            {
                var port = ports[currentPort];
                if (line.StartsWith("switchport port-security", StringComparison.OrdinalIgnoreCase) || line.StartsWith("switchport voice vlan") || line.StartsWith("authentication ") || line.StartsWith("dot1x ") || line == "mab" || line.StartsWith("channel-group "))
                {
                    var policy = security.GetValueOrDefault(currentPort) ?? new(currentPort);
                    if (line == "switchport port-security") policy = policy with { Enabled = true };
                    else if (SecureMac().Match(line) is { Success: true } m) secure.Add((MacRules.Normalize(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : null, currentPort));
                    else if (SecurityMaximum().Match(line) is { Success: true } count) policy = policy with { Maximum = int.Parse(count.Groups[1].Value) };
                    else if (SecurityViolation().Match(line) is { Success: true } violation) policy = policy with { Violation = violation.Groups[1].Value };
                    else policy = policy with { HasUnsupportedOptions = true };
                    security[currentPort] = policy;
                }
                if (line.StartsWith("description ", StringComparison.OrdinalIgnoreCase)) port = port with { Description = line[12..].Trim() };
                else if (line == "shutdown") port = port with { Enabled = false };
                else if (line is "undo shutdown" or "no shutdown") port = port with { Enabled = true };
                else if (line == "port link-type hybrid") port = port with { Mode = PortMode.Hybrid };
                else if (line is "no switchport" or "port link-mode route") port = port with { Mode = PortMode.Routed };
                else if (line is "port link-type access" or "switchport mode access") port = port with { Mode = PortMode.Access, TaggedVlans = [] };
                else if (line is "port link-type trunk" or "switchport mode trunk") port = port with { Mode = PortMode.Trunk, TaggedVlans = device.Vendor is Vendor.Cisco or Vendor.Ruijie ? Enumerable.Range(1, 4094).ToArray() : [1], IsUplink = true };
                else if (PvidLine().IsMatch(line)) port = port with { Pvid = int.Parse(PvidLine().Match(line).Groups[1].Value) };
                else if (AllowedLine().Match(line) is { Success: true } allowed)
                {
                    var value = allowed.Groups[2].Value;
                    var ids = VlanRanges.Parse(value.Replace(" to ", "-", StringComparison.OrdinalIgnoreCase), true);
                    var action = allowed.Groups[1].Value;
                    port = port with { TaggedVlans = action.Contains("undo", StringComparison.OrdinalIgnoreCase) || value.StartsWith("remove ", StringComparison.OrdinalIgnoreCase) ? port.TaggedVlans.Except(ids).Order().ToArray()
                        : device.Vendor is Vendor.Huawei or Vendor.H3C || value.StartsWith("add ", StringComparison.OrdinalIgnoreCase) ? port.TaggedVlans.Concat(ids).Distinct().Order().ToArray() : ids };
                }
                ports[currentPort] = port; continue;
            }
            if (line.StartsWith("vlan batch ", StringComparison.OrdinalIgnoreCase)) { foreach (var id in VlanRanges.Parse(line[11..].Replace(" to ", "-"))) vlans.TryAdd(id, new(id, $"VLAN{id}")); currentVlan = null; }
            else if (VlanLine().Match(line) is { Success: true } match) { var ids = VlanRanges.Parse(match.Groups[1].Value); foreach (var id in ids) vlans.TryAdd(id, new(id, $"VLAN{id}")); currentVlan = ids.Length == 1 ? ids[0] : null; }
            else if (currentVlan is int id && (line.StartsWith("name ") || line.StartsWith("description "))) vlans[id] = new(id, line[(line.IndexOf(' ') + 1)..].Trim());
        }
        if (ports.Count == 0 && !allowEmpty) throw new InvalidOperationException("配置中未解析出受支持的物理端口。已保留只读结果，未开放图形化下发。");
        foreach (var s in secure) macs.Add(new(s.Address, s.Vlan ?? ports[s.Port].Pvid, s.Port, MacBindingKind.SecureSource));
        var mapped = macs.Select(b => b with { PortName = ports.Keys.FirstOrDefault(n => n.Equals(b.PortName, StringComparison.OrdinalIgnoreCase)) ?? b.PortName }).ToArray();
        return new(device.Id, revision, ports.Values.ToArray(), vlans.Values.OrderBy(v => v.Id).ToArray(), DateTimeOffset.UtcNow, mapped, security.Values.ToArray());
    }
    [GeneratedRegex(@"^mac-address static (\S+) (?:interface )?([A-Za-z][A-Za-z-]*\s*[0-9/.:]+) vlan (\d+)$", RegexOptions.IgnoreCase)] private static partial Regex StaticMacVrp();
    [GeneratedRegex(@"^mac(?: |-)?address-table static (\S+) vlan (\d+) (?:interface|int) ([A-Za-z][A-Za-z-]*\s*[0-9/.:]+)$", RegexOptions.IgnoreCase)] private static partial Regex StaticMacIos();
    [GeneratedRegex(@"^switchport port-security mac-address ([0-9a-fA-F.:-]+)(?: vlan (\d+))?$", RegexOptions.IgnoreCase)] private static partial Regex SecureMac();
    [GeneratedRegex(@"^switchport port-security maximum (\d+)$")] private static partial Regex SecurityMaximum();
    [GeneratedRegex(@"^switchport port-security violation (restrict|protect|shutdown)$")] private static partial Regex SecurityViolation();
    [GeneratedRegex(@"^(?:GigabitEthernet|Ten-GigabitEthernet|TenGigabitEthernet|XGigabitEthernet|FastEthernet|Ethernet|FortyGigabitEthernet|HundredGigE|TwentyFiveGigE|GE|XGE|10GE|25GE|40GE|100GE)\d+(?:/\d+){1,3}$", RegexOptions.IgnoreCase)] private static partial Regex PhysicalPort();
    [GeneratedRegex(@"^(?:port default vlan|port access vlan|port trunk pvid vlan|switchport access vlan|switchport trunk native vlan)\s+(\d+)$", RegexOptions.IgnoreCase)] private static partial Regex PvidLine();
    [GeneratedRegex(@"^((?:undo )?port trunk (?:allow-pass|permit) vlan|switchport trunk allowed vlan)\s+(.+)$", RegexOptions.IgnoreCase)] private static partial Regex AllowedLine();
    [GeneratedRegex(@"^vlan\s+([0-9, -]+)$", RegexOptions.IgnoreCase)] private static partial Regex VlanLine();
}

public static class VlanRanges
{
    public static int[] Parse(string text, bool allowKeywords = false)
    {
        if (allowKeywords) { text = Regex.Replace(text, @"^(?:add|remove)\s+", "", RegexOptions.IgnoreCase); if (text.Equals("all", StringComparison.OrdinalIgnoreCase)) return Enumerable.Range(1, 4094).ToArray(); if (text.Equals("none", StringComparison.OrdinalIgnoreCase)) return []; }
        HashSet<int> result = [];
        foreach (var part in text.Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-');
            if (range.Length > 2 || !int.TryParse(range[0], out var start) || !int.TryParse(range[^1], out var end) || start < 1 || end > 4094 || start > end) throw new ArgumentException("VLAN 使用 1–4094，可输入 10,20,30-40。");
            foreach (var id in Enumerable.Range(start, end - start + 1)) result.Add(id);
        }
        return result.Order().ToArray();
    }
}

public sealed class NetworkOperations(IWorkspace workspace, IDriverRegistry drivers, ISessionFactory sessions, ISecretVault vault, HttpClient http)
{
    public static string PrepareCommand(Vendor vendor) => vendor switch { Vendor.Huawei => "screen-length 0 temporary", Vendor.H3C => "screen-length disable", _ => "terminal length 0" };
    public static string SaveCommand(Vendor vendor) => vendor is Vendor.Huawei or Vendor.H3C ? "save force" : "write memory";
    public async Task<(string Raw, DeviceState? State, string? Warning)> ReadConfigurationAsync(Device device, ConnectionProfile profile, CancellationToken ct)
    {
        if (profile.Protocol != ConnectionProtocol.Ssh) throw new NotSupportedException("结构化端口/VLAN 读取当前使用 SSH；其他协议可在连接页或终端使用。");
        await using var cli = new CliConversation(sessions.Create(ConnectionProtocol.Ssh));
        await cli.OpenAsync(device, profile, ct);
        if (device.Vendor is not (Vendor.Huawei or Vendor.H3C or Vendor.Cisco or Vendor.Ruijie)) throw new NotSupportedException("此品牌请使用通用终端；专用图形化读取驱动尚未适配。");
        if (device.DriverId is null)
        {
            var version = await cli.ExecuteAsync(new CliPreviewDriver(device.Vendor).ReadCommand(ReadCommand.Version), ct);
            var identified = DriverRegistry.Identify(device.Vendor, version) ?? throw new NotSupportedException("已连接，但未识别出已适配的系统系列。请使用高级终端核对版本。");
            device = device with { DriverId = identified }; workspace.SaveDevice(device);
        }
        drivers.Resolve(device);
        await cli.ExecuteAsync(PrepareCommand(device.Vendor), ct);
        var inventory = await ReadInventoryAsync(cli, device, ct);
        var raw = await cli.ExecuteAsync(drivers.Resolve(device).ReadCommand(ReadCommand.RunningConfiguration), ct);
        try { return (raw, SwitchInventory.Merge(ConfigurationParser.Parse(device, raw, (workspace.GetState(device.Id)?.Revision ?? 0) + 1, inventory.Ports is not null), inventory.Ports, inventory.Members, inventory.Warning), null); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        { return (raw, null, "配置原文已读取，可查看或备份；结构化解析未通过，图形化配置未开放。请使用高级终端核对。"); }
    }
    private async Task<(string[]? Ports, StackMemberState[] Members, string? Warning)> ReadInventoryAsync(CliConversation cli, Device device, CancellationToken ct)
    {
        string[]? ports = null; StackMemberState[] members = []; List<string> warnings = [];
        try { ports = SwitchInventory.PhysicalPorts(await cli.ExecuteAsync(drivers.Resolve(device).ReadCommand(ReadCommand.Interfaces), ct), device.Vendor); }
        catch (InvalidOperationException) { warnings.Add("接口清单未采集，当前仅列出配置中已解析的端口，端口总数未确认。"); }
        try { members = SwitchInventory.Members(device.Vendor, await cli.ExecuteAsync(SwitchInventory.StackCommand(device.Vendor), ct)); if (members.Length == 0) warnings.Add("堆叠成员未识别，不能据此认定是单机。"); }
        catch (InvalidOperationException) { warnings.Add("堆叠信息读取失败，不能据此认定是单机。"); }
        return (ports, members, warnings.Count > 0 ? string.Join(" ", warnings) : null);
    }
    public async Task<string> ReadProtocolAsync(Device device, ConnectionProfile profile, CancellationToken ct)
    {
        profile.Validate();
        if (profile.Protocol == ConnectionProtocol.Netconf)
        {
            using var client = new NetConfClient(SshSecurity.Connection(device, profile, vault)) { OperationTimeout = TimeSpan.FromSeconds(25) };
            SshSecurity.Pin(client, profile); using var registration = ct.Register(client.Dispose);
            await client.ConnectAsync(ct);
            var xml = await Task.Run(() => client.SendReceiveRpc("<rpc message-id=\"1\" xmlns=\"urn:ietf:params:xml:ns:netconf:base:1.0\"><get-config><source><running/></source></get-config></rpc>").OuterXml, ct);
            if (Encoding.UTF8.GetByteCount(xml) > 2_000_000) throw new InvalidOperationException("NETCONF 只读响应超过 2 MB。");
            return xml;
        }
        if (profile.Protocol is not (ConnectionProtocol.Restconf or ConnectionProtocol.HttpApi)) throw new NotSupportedException("请使用读取配置或打开交互终端。");
        using var request = new HttpRequestMessage(HttpMethod.Get, profile.Endpoint);
        request.Headers.Accept.ParseAdd(profile.Protocol == ConnectionProtocol.Restconf ? "application/yang-data+json" : "application/json");
        if (profile.CredentialReference is not null)
        {
            var secret = JsonSerializer.Deserialize<DeviceCredential>(vault.Read(profile.CredentialReference))!;
            request.Headers.Authorization = string.IsNullOrWhiteSpace(secret.Username) ? new("Bearer", secret.Password) : new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(secret.Username + ":" + secret.Password)));
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"设备只读 API 返回 HTTP {(int)response.StatusCode}，请核对路径与认证。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0) { if (buffer.Length + count > 2_000_000) throw new InvalidOperationException("只读 API 响应超过 2 MB。"); await buffer.WriteAsync(chunk.AsMemory(0, count), timeout.Token); }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    public async Task<string> ApplyAsync(Device device, ConnectionProfile profile, ChangePlan plan, bool save, IProgress<string> progress, CancellationToken ct)
    {
        if (device.IsSimulation || plan.IsSimulation || profile.Protocol != ConnectionProtocol.Ssh || ChangeEngine.IdentityHash(device) != plan.TargetIdentityHash) throw new InvalidOperationException("计划或设备连接不匹配，请重新预览。");
        if (!device.GetConnections().Contains(profile)) throw new InvalidOperationException("连接配置已变化，请重新读取并审核。");
        var before = workspace.GetState(device.Id) ?? throw new InvalidOperationException("请先读取当前配置。");
        if (ChangeEngine.Hash(before) != plan.BaselineHash) throw new InvalidOperationException("本地基线已变化，请重新预览。");
        var target = ChangeEngine.ProjectBatch(device, before, ChangeEngine.Requests(plan));
        if (!ChangeEngine.CompileBatch(drivers.Resolve(device), before, ChangeEngine.Requests(plan)).SequenceEqual(plan.Commands)) throw new InvalidOperationException("命令与结构化请求不一致，已拒绝执行。");
        await using var cli = new CliConversation(sessions.Create(ConnectionProtocol.Ssh));
        await cli.OpenAsync(device, profile, ct); await cli.ExecuteAsync(PrepareCommand(device.Vendor), ct);
        progress.Report("重新读取并核对设备基线…");
        var inventory = before.InventoryCollected ? await ReadInventoryAsync(cli, device, ct) : ((string[]?)null, Array.Empty<StackMemberState>(), (string?)null);
        var raw = await cli.ExecuteAsync(drivers.Resolve(device).ReadCommand(ReadCommand.RunningConfiguration), ct);
        var current = ConfigurationParser.Parse(device, raw, allowEmpty: before.InventoryCollected);
        if (before.InventoryCollected)
        {
            if (inventory.Item1 is null) throw new InvalidOperationException("执行前未能重新采集接口清单，已停止下发。");
            current = SwitchInventory.Merge(current, inventory.Item1, inventory.Item2, inventory.Item3);
            if (SwitchInventory.Signature(current) != SwitchInventory.Signature(before)) throw new InvalidOperationException("堆叠成员、角色或端口清单已变化，请重新读取并审核。");
        }
        if (ChangeEngine.ConfigurationHash(current) != ChangeEngine.ConfigurationHash(before)) throw new InvalidOperationException("真实设备配置已变化，请刷新并重新审核。");
        workspace.AddSnapshot(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "真实变更前自动备份", raw));
        workspace.AddAudit(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "真实配置开始", $"计划 {plan.Id}；{plan.Commands.Length} 步"));
        try
        {
            for (var i = 0; i < plan.Commands.Length; i++) { progress.Report($"下发配置 {i + 1}/{plan.Commands.Length}…"); await cli.ExecuteAsync(plan.Commands[i], ct); }
            progress.Report("读取设备配置并验证目标…");
            var afterRaw = await cli.ExecuteAsync(drivers.Resolve(device).ReadCommand(ReadCommand.RunningConfiguration), ct);
            var after = ConfigurationParser.Parse(device, afterRaw, before.Revision + 1, before.InventoryCollected);
            if (before.InventoryCollected)
            {
                var finalInventory = await ReadInventoryAsync(cli, device, ct);
                if (finalInventory.Ports is null) throw new InvalidOperationException("下发后未能重新确认接口清单，验证未通过；未保存启动配置。");
                after = SwitchInventory.Merge(after, finalInventory.Ports, finalInventory.Members, finalInventory.Warning);
                if (SwitchInventory.Signature(after) != SwitchInventory.Signature(before)) throw new InvalidOperationException("下发后堆叠拓扑或端口清单已变化，验证未通过；未保存启动配置。");
            }
            if (ChangeEngine.ConfigurationHash(after) != ChangeEngine.ConfigurationHash(target)) throw new InvalidOperationException("下发后配置与目标不一致，可能已部分生效，请检查备份和终端；未执行自动回滚。");
            workspace.SaveState(after);
            var message = "下发与回读验证通过，尚未保存到启动配置。";
            if (save)
            {
                progress.Report("保存到启动配置…");
                try { await cli.ExecuteAsync(SaveCommand(device.Vendor), ct); message = "下发成功、回读验证通过，设备保存命令已成功返回。"; }
                catch (Exception) { message = "下发与回读验证通过，但保存未确认成功；请在终端检查启动配置。"; }
            }
            workspace.AddAudit(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "真实配置结果", message)); return message;
        }
        catch
        {
            workspace.AddAudit(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "真实配置未完成", "可能已部分生效，已保留变更前备份；未自动重试或回滚。")); throw;
        }
    }
}
