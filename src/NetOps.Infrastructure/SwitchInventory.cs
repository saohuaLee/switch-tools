using System.Text.RegularExpressions;
using NetOps.Domain;

namespace NetOps.Infrastructure;

public static partial class SwitchInventory
{
    public static string StackCommand(Vendor vendor) => vendor switch { Vendor.Huawei => "display stack", Vendor.H3C => "display irf", Vendor.Cisco => "show switch", Vendor.Ruijie => "show switch virtual", _ => throw new NotSupportedException("尚未适配此厂商的堆叠信息读取。") };
    public static string NormalizePort(string name)
    {
        name = name.Replace(" ", ""); var m = PortName().Match(name); if (!m.Success) return name;
        var type = m.Groups[1].Value.ToLowerInvariant() switch
        {
            "gi" or "ge" or "gigabitethernet" => "GigabitEthernet", "fa" or "fastethernet" => "FastEthernet",
            "te" or "xge" or "10ge" or "ten-gigabitethernet" or "tengigabitethernet" or "xgigabitethernet" => "TenGigabitEthernet",
            "twe" or "twentyfivegige" or "25ge" => "TwentyFiveGigE", "fo" or "fortygigabitethernet" or "40ge" => "FortyGigabitEthernet",
            "hu" or "hundredgige" or "100ge" => "HundredGigE", "et" or "eth" or "ethernet" => "Ethernet", _ => m.Groups[1].Value
        };
        // 保留 Huawei/H3C 的原生万兆命名交由显示/驱动处理；匹配键统一，实际命令使用基线名称。
        return type + m.Groups[2].Value;
    }
    public static string[] PhysicalPorts(string output, Vendor vendor = Vendor.Other)
    {
        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var match = InterfaceLine().Match(line.Trim());
            if (match.Success)
            {
                var name = NormalizePort(match.Groups[1].Value);
                if (vendor == Vendor.Huawei) name = name.Replace("TenGigabitEthernet", "XGigabitEthernet");
                if (vendor == Vendor.H3C) name = name.Replace("TenGigabitEthernet", "Ten-GigabitEthernet");
                ports.Add(name);
            }
        }
        if (ports.Count == 0) throw new InvalidOperationException("接口清单未解析出物理端口，端口总数未确认。");
        return ports.Order(NaturalPortComparer.Instance).ToArray();
    }
    public static StackMemberState[] Members(Vendor vendor, string output)
    {
        List<StackMemberState> members = [];
        if (vendor == Vendor.Ruijie)
        {
            foreach (var side in new[] { "Local", "Peer" })
            {
                var id = Regex.Match(output, $@"(?im)^\s*{side} switch number\s*:\s*(\d+)"); var role = Regex.Match(output, $@"(?im)^\s*{side} switch operational role\s*:\s*(\S+)");
                if (id.Success && role.Success) { var r = role.Groups[1].Value.ToUpperInvariant(); members.Add(new(int.Parse(id.Groups[1].Value), r, r is "ACTIVE" or "STANDBY" ? "在线" : "状态待核对", Online: r is "ACTIVE" or "STANDBY" ? true : null)); }
            }
        }
        else foreach (var line in output.Split('\n'))
        {
            var m = MemberRow().Match(line.Trim()); if (!m.Success) continue;
            var fields = m.Groups[3].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); var role = m.Groups[2].Value;
            var identity = fields.FirstOrDefault(f => Regex.IsMatch(f, @"^(?:[a-fA-F0-9]{4}[.-]){2}[a-fA-F0-9]{4}$"));
            var status = vendor == Vendor.Cisco ? fields.LastOrDefault() ?? "未知" : role.Equals("Loading", StringComparison.OrdinalIgnoreCase) ? "加载中" : "在线";
            var online = vendor == Vendor.Cisco ? status.Equals("Ready", StringComparison.OrdinalIgnoreCase) : !role.Equals("Loading", StringComparison.OrdinalIgnoreCase);
            members.Add(new(int.Parse(m.Groups[1].Value), role, status, vendor == Vendor.Huawei ? fields.LastOrDefault() : null, identity, Online: online));
        }
        return members.DistinctBy(m => m.Id).OrderBy(m => m.Id).ToArray();
    }
    public static int? MemberId(string portName, IReadOnlyList<StackMemberState> members)
    {
        var m = PortName().Match(portName.Replace(" ", "")); if (!m.Success || !int.TryParse(m.Groups[2].Value.Split('/')[0], out var id)) return null;
        return members.Any(s => s.Id == id) ? id : null;
    }
    public static DeviceState Merge(DeviceState state, string[]? physicalPorts, StackMemberState[] members, string? warning)
    {
        var configured = state.Ports.ToDictionary(p => NormalizePort(p.Name), StringComparer.OrdinalIgnoreCase);
        var names = physicalPorts ?? configured.Keys.ToArray();
        // 只有运行接口清单中的端口可操作；离线/预配置成员的端口不混入在线面板。
        var ports = names.Where(n => MemberId(n, members) is not int id || members.Single(m => m.Id == id).Online != false).Select(n =>
        {
            if (configured.TryGetValue(NormalizePort(n), out var p)) return p;
            return new PortState(n, "", true, false, 0, PortMode.Unknown, 1, [], Extensions: new() { ["source"] = "interface-inventory", ["configuration"] = "缺省模式未确认，只支持描述与启停" });
        }).OrderBy(p => NormalizePort(p.Name), NaturalPortComparer.Instance).ToArray();
        var counted = members.Select(m => m with { PortCount = physicalPorts is null || m.Online != true ? null : physicalPorts.Count(n => MemberId(n, members) == m.Id) }).ToArray();
        return state with { Ports = ports, StackMembers = counted, InventoryCollected = physicalPorts is not null, InventoryWarning = warning };
    }
    public static string Signature(DeviceState state) => string.Join(";", (state.StackMembers ?? []).OrderBy(m => m.Id).Select(m => $"{m.Id}:{m.Role}:{m.Identity}:{m.Online}")) + "|" + string.Join(",", state.Ports.Select(p => NormalizePort(p.Name)).Order(StringComparer.Ordinal));
    [GeneratedRegex(@"^([A-Za-z-]+|\d+GE)(\d+(?:/\d+){1,3})$", RegexOptions.IgnoreCase)] private static partial Regex PortName();
    [GeneratedRegex(@"^((?:GigabitEthernet|Ten-GigabitEthernet|TenGigabitEthernet|XGigabitEthernet|FastEthernet|FortyGigabitEthernet|HundredGigE|TwentyFiveGigE|Ethernet|Gi|GE|XGE|10GE|25GE|40GE|100GE|Te|Fa|Fo|Hu|Twe|Eth|Et)\s*\d+(?:/\d+){1,3})(?:\s+|$)", RegexOptions.IgnoreCase)] private static partial Regex InterfaceLine();
    [GeneratedRegex(@"^[*+ ]*(\d+)\s+(?:\d+\s+)?(Master|Standby|Slave|Active|Member|Loading)\s+(.+)$", RegexOptions.IgnoreCase)] private static partial Regex MemberRow();
    private sealed class NaturalPortComparer : IComparer<string>
    {
        public static NaturalPortComparer Instance { get; } = new();
        public int Compare(string? x, string? y)
        {
            var a = Regex.Split(x ?? "", @"(\d+)"); var b = Regex.Split(y ?? "", @"(\d+)");
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++) { var n = int.TryParse(a[i], out var av) && int.TryParse(b[i], out var bv) ? av.CompareTo(bv) : StringComparer.OrdinalIgnoreCase.Compare(a[i], b[i]); if (n != 0) return n; } return a.Length.CompareTo(b.Length);
        }
    }
}
