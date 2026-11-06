using NetOps.Application;
using NetOps.Domain;

namespace NetOps.Infrastructure;

/// <summary>静态注册，不加载任意外部程序集；新驱动通过接口注册，不改业务和界面。</summary>
public sealed class DriverRegistry : IDriverRegistry
{
    private readonly Dictionary<Vendor, IDeviceDriver> drivers = new[] { Vendor.Huawei, Vendor.H3C, Vendor.Cisco, Vendor.Ruijie }.ToDictionary(v => v, v => (IDeviceDriver)new CliPreviewDriver(v));
    public IDeviceDriver Resolve(Device device)
    {
        var allowed = device.Vendor switch { Vendor.Huawei => new[] { "huawei.vrp" }, Vendor.H3C => ["h3c.comware"], Vendor.Cisco => ["cisco.ios", "cisco.iosxe"], Vendor.Ruijie => ["ruijie.rgos"], _ => [] };
        if (!drivers.TryGetValue(device.Vendor, out var driver) || (!device.IsSimulation && !allowed.Contains(device.DriverId))) throw new NotSupportedException("当前系统系列尚未识别或适配，不能使用图形化配置；可继续使用通用高级终端。");
        return driver;
    }
    public static string? Identify(Vendor vendor, string version) => vendor switch
    {
        Vendor.Huawei when version.Contains("VRP", StringComparison.OrdinalIgnoreCase) || version.Contains("Versatile Routing Platform", StringComparison.OrdinalIgnoreCase) => "huawei.vrp",
        Vendor.H3C when version.Contains("Comware", StringComparison.OrdinalIgnoreCase) => "h3c.comware",
        Vendor.Cisco when version.Contains("IOS XE", StringComparison.OrdinalIgnoreCase) || version.Contains("IOS-XE", StringComparison.OrdinalIgnoreCase) => "cisco.iosxe",
        Vendor.Cisco when version.Contains("IOS Software", StringComparison.OrdinalIgnoreCase) => "cisco.ios",
        Vendor.Ruijie when version.Contains("RGOS", StringComparison.OrdinalIgnoreCase) => "ruijie.rgos", _ => null
    };
    public IReadOnlyList<DriverDescriptor> Descriptors { get; } = Enum.GetValues<Vendor>().Select(v => new DriverDescriptor(v,
        v switch { Vendor.Huawei => "VRP", Vendor.H3C => "Comware", Vendor.Cisco => "IOS / IOS XE", Vendor.Ruijie => "RGOS", Vendor.Juniper => "Junos", Vendor.MikroTik => "RouterOS", Vendor.Ubiquiti => "UniFi", _ => "按产品系列适配" },
        v is Vendor.Huawei or Vendor.H3C or Vendor.Cisco or Vendor.Ruijie ? "L1 会话 + 实验性 L2" : "通用会话接入，专用驱动待适配",
        v is Vendor.Huawei or Vendor.H3C or Vendor.Cisco or Vendor.Ruijie ? "按 VRP / Comware / IOS / IOS XE / RGOS 识别；端口/VLAN 读写及回读流程已有会话夹具验证，未做真机认证" : "SSH / Console / Telnet 通用终端可使用；专有 API、结构化解析和图形化配置未适配")).ToArray();
}

public sealed class CliPreviewDriver(Vendor vendor) : IDeviceDriver
{
    public Vendor Vendor => vendor;
    private bool IsVrp => vendor is Vendor.Huawei or Vendor.H3C;
    public string[] Compile(ChangeRequest request, DeviceState? baseline = null)
    {
        List<string> commands = [IsVrp ? "system-view" : "configure terminal"];
        if ((request.CreateVlanId ?? request.RenameVlanId) is int vlan)
        {
            commands.Add($"vlan {vlan}");
            commands.Add($"{(vendor == Vendor.Huawei ? "description" : "name")} {request.VlanName}");
            commands.Add(IsVrp ? "quit" : "exit");
        }
        if (request.DeleteVlanId is int remove) commands.Add($"{(IsVrp ? "undo" : "no")} vlan {remove}");
        foreach (var b in (request.RemoveMacBindings ?? []).Where(b => b.Kind == MacBindingKind.StaticForwarding)) commands.Add(StaticMac(b, true));
        foreach (var b in (request.AddMacBindings ?? []).Where(b => b.Kind == MacBindingKind.StaticForwarding)) commands.Add(StaticMac(b, false));
        foreach (var port in request.Ports ?? [])
        {
            if (request.MigratePortSecurity && MacRules.SecurityChanges(request).Contains(port)) continue;
            commands.Add($"interface {port}");
            if (request.Description is not null) commands.Add(request.Description.Length == 0 ? (IsVrp ? "undo description" : "no description") : $"description {request.Description}");
            var access = (request.Mode ?? baseline?.Ports.Single(p => p.Name == port).Mode) == PortMode.Access;
            var pvid = request.Pvid ?? baseline?.Ports.Single(p => p.Name == port).Pvid;
            if (IsVrp)
            {
                if (request.Mode is not null) commands.Add($"port link-type {(access ? "access" : "trunk")}");
                if (request.Pvid is not null || request.Mode is not null) commands.Add(access ? $"port {(vendor == Vendor.H3C ? "access" : "default")} vlan {pvid}" : $"port trunk pvid vlan {pvid}");
                if (!access && request.TaggedVlans is not null) { commands.Add($"undo port trunk {(vendor == Vendor.H3C ? "permit" : "allow-pass")} vlan all"); commands.Add($"port trunk {(vendor == Vendor.H3C ? "permit" : "allow-pass")} vlan {string.Join(" ", request.TaggedVlans)}"); }
            }
            else
            {
                if (request.Mode is not null) commands.Add($"switchport mode {(access ? "access" : "trunk")}");
                if (request.Pvid is not null || request.Mode is not null) commands.Add(access ? $"switchport access vlan {pvid}" : $"switchport trunk native vlan {pvid}");
                if (!access && request.TaggedVlans is not null) commands.Add($"switchport trunk allowed vlan {string.Join(",", request.TaggedVlans)}");
            }
            if (request.Enabled is bool enabled) commands.Add(enabled ? (IsVrp ? "undo shutdown" : "no shutdown") : "shutdown");
            commands.Add(IsVrp ? "quit" : "exit");
        }
        var securityPorts = MacRules.SecurityChanges(request);
        void RemoveSecure(string name)
        {
            commands.Add($"interface {name}"); commands.Add("shutdown");
            foreach (var b in (request.RemoveMacBindings ?? []).Where(b => b.Kind == MacBindingKind.SecureSource && b.PortName == name)) commands.Add($"no switchport port-security mac-address {MacRules.VendorAddress(b.Address, false)}");
            if (request.MigratePortSecurity) commands.Add("no switchport port-security");
            commands.Add("switchport mode access");
            if (request.MigratePortSecurity) commands.Add($"switchport access vlan {request.Pvid}");
        }
        if (securityPorts.Length > 1)
            foreach (var name in securityPorts) { RemoveSecure(name); commands.Add("exit"); }
        foreach (var name in securityPorts)
        {
            if (vendor != Vendor.Cisco || baseline is null) throw new NotSupportedException("源 MAC 白名单需要 Cisco IOS / IOS XE 和已读取的基线。");
            var bindings = MacRules.TargetBindings(baseline, request).Where(b => b.Kind == MacBindingKind.SecureSource && b.PortName == name).ToArray();
            if (securityPorts.Length == 1) RemoveSecure(name); else commands.Add($"interface {name}");
            if (bindings.Length > 0)
            {
                var policy = MacRules.TargetSecurity(baseline, request).Single(p => p.PortName == name);
                commands.Add("switchport port-security"); commands.Add($"switchport port-security maximum {policy.Maximum}"); commands.Add($"switchport port-security violation {policy.Violation}");
                foreach (var b in (request.AddMacBindings ?? []).Where(b => b.Kind == MacBindingKind.SecureSource && b.PortName == name)) commands.Add($"switchport port-security mac-address {MacRules.VendorAddress(b.Address, false)}");
            }
            else { commands.Add("no switchport port-security"); commands.Add("no switchport port-security maximum"); commands.Add("no switchport port-security violation"); }
            if (!request.MigratePortSecurity || baseline.Ports.Single(p => p.Name == name).Enabled) commands.Add("no shutdown");
            commands.Add("exit");
        }
        commands.Add(IsVrp ? "return" : "end");
        return commands.ToArray();
    }
    private string StaticMac(MacBinding b, bool remove)
    {
        var prefix = remove ? IsVrp ? "undo " : "no " : "";
        var mac = MacRules.VendorAddress(b.Address, IsVrp);
        if (IsVrp) return $"{prefix}mac-address static {mac} {(vendor == Vendor.H3C ? "interface " : "")}{b.PortName} vlan {b.VlanId}";
        return $"{prefix}{(vendor == Vendor.Cisco ? "mac address-table" : "mac-address-table")} static {mac} vlan {b.VlanId} interface {b.PortName}";
    }
    public string ReadCommand(ReadCommand command) => (IsVrp, command) switch
    {
        (true, Domain.ReadCommand.Version) => "display version",
        (true, Domain.ReadCommand.Interfaces) => "display interface brief",
        (true, Domain.ReadCommand.Vlans) => "display vlan",
        (true, Domain.ReadCommand.RunningConfiguration) => "display current-configuration",
        (false, Domain.ReadCommand.Version) => "show version",
        (false, Domain.ReadCommand.Interfaces) => "show interfaces status",
        (false, Domain.ReadCommand.Vlans) => "show vlan",
        (false, Domain.ReadCommand.RunningConfiguration) => "show running-config",
        _ => throw new ArgumentException("只读命令无效。")
    };
}
