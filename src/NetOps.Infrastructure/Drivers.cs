using NetOps.Application;
using NetOps.Domain;

namespace NetOps.Infrastructure;

/// <summary>静态注册，不加载任意外部程序集；新驱动通过接口注册，不改业务和界面。</summary>
public sealed class DriverRegistry : IDriverRegistry
{
    private readonly Dictionary<Vendor, IDeviceDriver> drivers = new[] { Vendor.Huawei, Vendor.H3C, Vendor.Cisco, Vendor.Ruijie }.ToDictionary(v => v, v => (IDeviceDriver)new CliPreviewDriver(v));
    public IDeviceDriver Resolve(Device device) => drivers.TryGetValue(device.Vendor, out var driver) ? driver : throw new NotSupportedException("此厂商的驱动尚未实现，可登记设备档案，暂不能读取或生成配置。");
    public IReadOnlyList<DriverDescriptor> Descriptors { get; } = Enum.GetValues<Vendor>().Select(v => new DriverDescriptor(v,
        v switch { Vendor.Huawei => "VRP", Vendor.H3C => "Comware", Vendor.Cisco => "IOS / IOS XE", Vendor.Ruijie => "RGOS", Vendor.Juniper => "Junos", Vendor.MikroTik => "RouterOS", Vendor.Ubiquiti => "UniFi", _ => "按产品系列适配" },
        v is Vendor.Huawei or Vendor.H3C or Vendor.Cisco or Vendor.Ruijie ? "实验性预览 / SSH 只读" : "计划支持",
        v is Vendor.Huawei or Vendor.H3C or Vendor.Cisco or Vendor.Ruijie ? "模拟端口与 VLAN；未验证任何真实型号/固件" : "当前仅设备档案；协议与配置功能待实现")).ToArray();
}

public sealed class CliPreviewDriver(Vendor vendor) : IDeviceDriver
{
    public Vendor Vendor => vendor;
    private bool IsVrp => vendor is Vendor.Huawei or Vendor.H3C;
    public string[] Compile(ChangeRequest request)
    {
        List<string> commands = [IsVrp ? "system-view" : "configure terminal"];
        if (request.CreateVlanId is int vlan)
        {
            commands.Add($"vlan {vlan}");
            commands.Add($"{(vendor == Vendor.Huawei ? "description" : "name")} {request.VlanName}");
            commands.Add(IsVrp ? "quit" : "exit");
        }
        foreach (var port in request.Ports ?? [])
        {
            commands.Add($"interface {port}");
            if (request.Description is not null) commands.Add($"description {request.Description}");
            var access = request.Mode == PortMode.Access;
            if (IsVrp)
            {
                commands.Add($"port link-type {(access ? "access" : "trunk")}");
                commands.Add(access ? $"port default vlan {request.Pvid}" : $"port trunk pvid vlan {request.Pvid}");
                if (!access) commands.Add($"port trunk {(vendor == Vendor.H3C ? "permit" : "allow-pass")} vlan {string.Join(" ", request.TaggedVlans ?? [])}");
            }
            else
            {
                commands.Add($"switchport mode {(access ? "access" : "trunk")}");
                commands.Add(access ? $"switchport access vlan {request.Pvid}" : $"switchport trunk native vlan {request.Pvid}");
                if (!access) commands.Add($"switchport trunk allowed vlan {string.Join(",", request.TaggedVlans ?? [])}");
            }
            if (request.Enabled is bool enabled) commands.Add(enabled ? (IsVrp ? "undo shutdown" : "no shutdown") : "shutdown");
            commands.Add(IsVrp ? "quit" : "exit");
        }
        commands.Add(IsVrp ? "return" : "end");
        return commands.ToArray();
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
