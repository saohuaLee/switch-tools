using System.Text.RegularExpressions;
using System.Text;
using NetOps.Domain;

namespace NetOps.Application;

public static partial class MacRules
{
    public static string Normalize(string address)
    {
        var normalized = (address ?? "").Normalize(NormalizationForm.FormKC);
        var hex = new string(normalized.Where(c => !char.IsWhiteSpace(c) && c is not (':' or '-' or '.' or '\u2010' or '\u2011' or '\u2013' or '\u2014' or '\u2212')).ToArray()).ToLowerInvariant();
        if (!MacFormat().IsMatch(hex)) throw new ArgumentException("MAC 地址必须包含 12 位十六进制字符；支持冒号、横线、点号、空格及不带分隔符，不能猜测缺失字符。");
        if (hex == "000000000000" || (Convert.ToByte(hex[..2], 16) & 1) != 0) throw new ArgumentException("只能绑定有效的单播 MAC 地址，不能使用全零、广播或组播地址。");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
    public static MacBinding Canonical(MacBinding b) => b with { Address = Normalize(b.Address), PortName = b.PortName.Trim() };
    public static string Key(MacBinding b) => $"{Normalize(b.Address)}/{b.VlanId}";
    public static MacBinding[] Bindings(DeviceState state) => (state.MacBindings ?? []).Select(Canonical).OrderBy(Key).ToArray();
    public static PortSecurityState[] Security(DeviceState state) => (state.PortSecurity ?? []).Where(p => p.Enabled || p.HasUnsupportedOptions).OrderBy(p => p.PortName).ToArray();
    public static string VendorAddress(string address, bool hyphen) { var hex = Normalize(address).Replace(":", ""); return string.Join(hyphen ? "-" : ".", Enumerable.Range(0, 3).Select(i => hex.Substring(i * 4, 4))); }
    public static MacBinding[] TargetBindings(DeviceState state, ChangeRequest request) => Bindings(state).Except(request.RemoveMacBindings ?? []).Concat(request.AddMacBindings ?? []).OrderBy(Key).ToArray();
    public static string[] SecurityChanges(ChangeRequest request) => (request.AddMacBindings ?? []).Concat(request.RemoveMacBindings ?? []).Where(b => b.Kind == MacBindingKind.SecureSource).Select(b => b.PortName)
        .Concat((request.SecuritySettings ?? []).Select(s => s.PortName)).Distinct().Order().ToArray();
    public static PortSecurityState[] TargetSecurity(DeviceState state, ChangeRequest request)
    {
        var changes = SecurityChanges(request); var bindings = TargetBindings(state, request);
        return Security(state).Where(p => !changes.Contains(p.PortName)).Concat(changes.Select(name =>
        {
            var settings = request.SecuritySettings?.SingleOrDefault(s => s.PortName == name);
            var old = Security(state).SingleOrDefault(s => s.PortName == name);
            return new PortSecurityState(name, bindings.Any(b => b.PortName == name && b.Kind == MacBindingKind.SecureSource),
                settings?.Maximum ?? old?.Maximum ?? 1, settings?.Violation ?? old?.Violation ?? "shutdown");
        })).Where(p => p.Enabled || p.HasUnsupportedOptions).OrderBy(p => p.PortName).ToArray();
    }
    public static void Validate(Device device, DeviceState state, ChangeRequest request)
    {
        var old = Bindings(state); var add = request.AddMacBindings ?? []; var remove = request.RemoveMacBindings ?? [];
        if (add.Length + remove.Length > 1000) throw new ArgumentException("单次 MAC 变更最多 1000 条。");
        if (add.Distinct().Count() != add.Length || remove.Distinct().Count() != remove.Length) throw new ArgumentException("MAC 变更包含重复记录。");
        if (remove.Any(b => !old.Contains(b))) throw new ArgumentException("待删除的 MAC 绑定与当前配置不一致，请重新读取。");
        if (add.Any(b => remove.Contains(b))) throw new ArgumentException("同一条绑定不能同时添加与删除。");
        var settings = request.SecuritySettings ?? [];
        if (settings.Select(s => s.PortName).Distinct().Count() != settings.Length) throw new ArgumentException("同一端口只能设置一组端口安全参数。");
        if (settings.Any(s => s.Maximum < 1 || s.Violation is not ("shutdown" or "restrict" or "protect")))
            throw new ArgumentException("MAC 数量上限必须为正整数；违规动作只能选择 shutdown、restrict 或 protect。");
        if (request.MigratePortSecurity)
        {
            if (request.Pvid is null || request.Ports?.Length is not > 0 || request.Mode is not null || request.Enabled is not null || request.Description is not null || request.TaggedVlans is not null || SecurityChanges(request).Length == 0)
                throw new ArgumentException("安全绑定迁移仅允许更换接入口所属网络，并保留原启停状态及安全策略。");
            var expected = old.Where(b => b.Kind == MacBindingKind.SecureSource && request.Ports.Contains(b.PortName)).ToArray();
            var wanted = expected.Select(b => b with { VlanId = request.Pvid.Value }).ToArray();
            if (!settings.Select(s => s.PortName).Order().SequenceEqual(expected.Select(b => b.PortName).Distinct().Order()))
                throw new ArgumentException("迁移安全策略必须与所选端口的原绑定完全对应，不能夹带其他端口。");
            if (!remove.OrderBy(Key).SequenceEqual(expected.OrderBy(Key)) || !add.OrderBy(Key).SequenceEqual(wanted.OrderBy(Key)))
                throw new ArgumentException("更换网络必须完整保留所选端口的全部静态安全绑定，不能同时更换地址或遗漏绑定。");
            foreach (var name in SecurityChanges(request))
            {
                var policy = Security(state).SingleOrDefault(p => p.PortName == name);
                if (policy is null || !policy.Enabled || settings.SingleOrDefault(s => s.PortName == name) != new PortSecuritySettings(name, policy.Maximum, policy.Violation))
                    throw new ArgumentException("更换网络必须保留原有 MAC 数量上限和违规动作。");
            }
        }
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
            var p = targetState.Ports.SingleOrDefault(p => p.Name == name) ?? throw new ArgumentException("端口安全参数包含未知端口。");
            if (p.Mode != PortMode.Access || p.IsUplink) throw new ArgumentException("端口安全绑定当前只允许明确的接入口，不能用于干道、上联或未知模式端口。");
            var policy = Security(state).FirstOrDefault(s => s.PortName == name);
            if (policy is not null && (policy.HasUnsupportedOptions || policy.Enabled && policy.Violation is not ("shutdown" or "restrict" or "protect")))
                throw new ArgumentException("此端口已有 sticky、认证或其他未适配的安全选项，请先在终端核对；图形化配置不会覆盖这些选项。");
            var count = target.Count(b => b.PortName == name && b.Kind == MacBindingKind.SecureSource);
            if (count > 64) throw new ArgumentException("当前每端口最多编辑 64 条静态安全 MAC；设备资源上限仍以实际型号为准。");
            if (count == 0 && settings.Any(s => s.PortName == name)) throw new ArgumentException("设置端口安全参数时，端口必须至少保留一条静态安全 MAC 绑定。");
            var maximum = settings.SingleOrDefault(s => s.PortName == name)?.Maximum ?? policy?.Maximum ?? 1;
            if (count > maximum) throw new ArgumentException($"{name} 最终有 {count} 条绑定，超过 MAC 数量上限 {maximum}，请提高上限或减少绑定。");
        }
        foreach (var p in Security(state).Where(p => p.Enabled))
            if (!request.MigratePortSecurity && request.Ports?.Contains(p.PortName) == true && (request.Pvid is not null || request.Mode is not null || request.TaggedVlans is not null)) throw new ArgumentException("此端口已绑定设备地址。请使用“更换端口所属网络”并选择保留绑定，或到高级设置核对安全策略。");
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
    [GeneratedRegex(@"\A[0-9a-f]{12}\z")] private static partial Regex MacFormat();
}
