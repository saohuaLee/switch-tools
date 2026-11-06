using NetOps.Domain;

namespace NetOps.Application;

/// <summary>一次只处理一件事；基于真实读取状态生成明确的配置请求。</summary>
public static class BeginnerOperations
{
    public static ChangeRequest MoveNetwork(DeviceState state, string[] names, int vlan, bool create, bool keepBindings)
    {
        var ports = names.Select(name => state.Ports.SingleOrDefault(p => p.Name == name) ?? throw new ArgumentException("找不到所选端口，请重新读取设备。")).ToArray();
        if (ports.Length == 0) throw new ArgumentException("请先选择要更换网络的端口。");
        if (ports.Any(p => p.Mode != PortMode.Access || p.IsUplink)) throw new ArgumentException("普通换网络只用于连接电脑、打印机等终端的接入口；连接其他交换机的端口请使用高级设置。");
        var changed = ports.Where(p => p.Pvid != vlan).Select(p => p.Name).ToArray();
        if (changed.Length == 0) throw new ArgumentException("所选端口已经属于这个网络，不需要修改。");
        if (!state.Vlans.Any(v => v.Id == vlan) && !create) throw new ArgumentException("设备中还没有这个网络，请选择现有网络，或勾选同时创建。");
        var bindings = MacRules.Bindings(state).Where(b => changed.Contains(b.PortName)).ToArray();
        if (bindings.Any(b => b.Kind == MacBindingKind.StaticForwarding)) throw new ArgumentException("所选端口有高级静态转发映射，不能自动推断其用途。请在高级 MAC 设置中核对后再更换网络。");
        var policies = MacRules.Security(state).Where(p => changed.Contains(p.PortName)).ToArray();
        if (policies.Any(p => p.HasUnsupportedOptions || !p.Enabled || !bindings.Any(b => b.PortName == p.PortName)))
            throw new ArgumentException("所选端口含自动学习或未适配的认证 / 安全配置，无法完整恢复其运行状态。请使用高级设置核对，不会自动清除这些配置。");
        if (bindings.Length > 0 && !keepBindings) throw new ArgumentException("这些端口已经绑定设备地址。请勾选“保留已绑定设备并一起迁移”，避免更换网络后丢失绑定。");
        return new(CreateVlanId: state.Vlans.Any(v => v.Id == vlan) ? null : vlan, VlanName: state.Vlans.Any(v => v.Id == vlan) ? null : $"VLAN{vlan}",
            Ports: changed, Pvid: vlan, RemoveMacBindings: bindings, AddMacBindings: bindings.Select(b => b with { VlanId = vlan }).ToArray(),
            SecuritySettings: policies.Select(p => new PortSecuritySettings(p.PortName, p.Maximum, p.Violation)).ToArray(), MigratePortSecurity: bindings.Length > 0);
    }
}
