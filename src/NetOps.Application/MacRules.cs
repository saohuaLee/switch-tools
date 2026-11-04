using System.Text.RegularExpressions;
using NetOps.Domain;

namespace NetOps.Application;

public static partial class MacRules
{
    public static string Normalize(string address)
    {
        if (!MacFormat().IsMatch(address)) throw new ArgumentException("MAC 地址格式应为 00:11:22:33:44:55、0011-2233-4455 或 0011.2233.4455。");
        var hex = address.Replace(":", "").Replace("-", "").Replace(".", "").ToLowerInvariant();
        if (hex == "000000000000" || (Convert.ToByte(hex[..2], 16) & 1) != 0) throw new ArgumentException("只能绑定有效的单播 MAC 地址，不能使用全零、广播或组播地址。");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
    public static MacBinding Canonical(MacBinding b) => b with { Address = Normalize(b.Address), PortName = b.PortName.Trim() };
    public static string Key(MacBinding b) => $"{Normalize(b.Address)}/{b.VlanId}";
    public static MacBinding[] Bindings(DeviceState state) => (state.MacBindings ?? []).Select(Canonical).OrderBy(Key).ToArray();
    public static PortSecurityState[] Security(DeviceState state) => (state.PortSecurity ?? []).Where(p => p.Enabled || p.HasUnsupportedOptions).OrderBy(p => p.PortName).ToArray();
    public static string VendorAddress(string address, bool hyphen) { var hex = Normalize(address).Replace(":", ""); return string.Join(hyphen ? "-" : ".", Enumerable.Range(0, 3).Select(i => hex.Substring(i * 4, 4))); }
    public static MacBinding[] TargetBindings(DeviceState state, ChangeRequest request) => Bindings(state).Except(request.RemoveMacBindings ?? []).Concat(request.AddMacBindings ?? []).OrderBy(Key).ToArray();
    public static string[] SecurityChanges(ChangeRequest request) => (request.AddMacBindings ?? []).Concat(request.RemoveMacBindings ?? []).Where(b => b.Kind == MacBindingKind.SecureSource).Select(b => b.PortName).Distinct().Order().ToArray();
    public static PortSecurityState[] TargetSecurity(DeviceState state, ChangeRequest request)
    {
        var changes = SecurityChanges(request); var bindings = TargetBindings(state, request);
        return Security(state).Where(p => !changes.Contains(p.PortName)).Concat(changes.Select(name => new PortSecurityState(name,
            bindings.Any(b => b.PortName == name && b.Kind == MacBindingKind.SecureSource), bindings.Count(b => b.PortName == name && b.Kind == MacBindingKind.SecureSource), "restrict"))).Where(p => p.Enabled || p.HasUnsupportedOptions).OrderBy(p => p.PortName).ToArray();
    }
    public static void Validate(Device device, DeviceState state, ChangeRequest request)
    {
        var old = Bindings(state); var add = request.AddMacBindings ?? []; var remove = request.RemoveMacBindings ?? [];
        if (add.Length + remove.Length > 1000) throw new ArgumentException("单次 MAC 变更最多 1000 条。");
        if (add.Distinct().Count() != add.Length || remove.Distinct().Count() != remove.Length) throw new ArgumentException("MAC 变更包含重复记录。");
        if (remove.Any(b => !old.Contains(b))) throw new ArgumentException("待删除的 MAC 绑定与当前配置不一致，请重新读取。");
        if (add.Any(b => remove.Contains(b))) throw new ArgumentException("同一条绑定不能同时添加与删除。");
        var target = TargetBindings(state, request);
        if (target.GroupBy(Key).Any(g => g.Count() > 1)) throw new ArgumentException("同一个 MAC 与 VLAN 只能绑定一个端口和一种用途；迁移请先删除旧绑定。");
        var targetState = ChangeEngine.TransformPortsAndVlans(state, request);
        foreach (var b in target)
        {
            if (!Enum.IsDefined(b.Kind)) throw new ArgumentException("MAC 绑定用途无效。");
            var port = targetState.Ports.SingleOrDefault(p => p.Name == b.PortName) ?? throw new ArgumentException("MAC 绑定包含未知端口，请使用完整端口名称。");
            if (!targetState.Vlans.Any(v => v.Id == b.VlanId)) throw new ArgumentException("MAC 绑定引用的 VLAN 不存在或将被删除。");
            // 未调整的非标准端口保留原有条目；新绑定与成员变更必须能确定 VLAN 关系。
            if (add.Contains(b) || request.Ports?.Contains(b.PortName) == true)
                if (port.Mode == PortMode.Access ? port.Pvid != b.VlanId : port.Mode != PortMode.Trunk || !port.TaggedVlans.Contains(b.VlanId)) throw new ArgumentException($"{b.PortName} 不属于绑定的 VLAN {b.VlanId}，请先调整端口成员关系。");
        }
        foreach (var name in SecurityChanges(request))
        {
            if (device.Vendor != Vendor.Cisco) throw new NotSupportedException("源 MAC 白名单当前适配 Cisco IOS / IOS XE 接入口；此厂商可使用静态转发绑定或高级终端。");
            var p = targetState.Ports.Single(p => p.Name == name);
            if (p.Mode != PortMode.Access || p.IsUplink) throw new ArgumentException("源 MAC 白名单当前只允许明确的接入口，不能用于干道、上联或未知模式端口。");
            var policy = Security(state).FirstOrDefault(s => s.PortName == name);
            if (policy is not null && (policy.HasUnsupportedOptions || policy.Enabled && (policy.Violation != "restrict" || policy.Maximum != old.Count(b => b.PortName == name && b.Kind == MacBindingKind.SecureSource))))
                throw new ArgumentException("此端口已有动态学习、sticky 或其他安全策略，请先在终端核对；图形化白名单不会覆盖现有策略。");
            if (target.Count(b => b.PortName == name && b.Kind == MacBindingKind.SecureSource) > 64) throw new ArgumentException("当前白名单每端口最多 64 个 MAC；设备资源上限仍以实际型号为准。");
        }
        foreach (var p in Security(state).Where(p => p.Enabled))
            if (request.Ports?.Contains(p.PortName) == true && (request.Pvid is not null || request.Mode is not null || request.TaggedVlans is not null)) throw new ArgumentException("启用端口安全时请先移除安全绑定并核对安全策略，再修改 VLAN 或模式。");
    }
    public static MacBinding[] ParseBatch(string text, MacBindingKind kind)
    {
        List<MacBinding> rows = [];
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Trim().Split([',', '，']);
            if (fields.Length != 3 || !int.TryParse(fields[1].Trim(), out var vlan)) throw new ArgumentException($"第 {rows.Count + 1} 行格式应为：MAC,VLAN,完整端口名称。");
            rows.Add(Canonical(new(fields[0].Trim(), vlan, fields[2].Trim(), kind)));
            if (rows.Count > 1000) throw new ArgumentException("每次最多输入 1000 条 MAC 绑定。");
        }
        if (rows.Count == 0) throw new ArgumentException("请填写至少一条 MAC、VLAN 与端口映射。");
        return rows.ToArray();
    }
    [GeneratedRegex(@"\A(?:[0-9A-Fa-f]{12}|(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}|(?:[0-9A-Fa-f]{4}-){2}[0-9A-Fa-f]{4}|(?:[0-9A-Fa-f]{4}\.){2}[0-9A-Fa-f]{4})\z")] private static partial Regex MacFormat();
}
