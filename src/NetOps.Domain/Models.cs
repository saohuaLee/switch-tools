using System.Net;
using System.Text.Json.Serialization;

namespace NetOps.Domain;

public enum Vendor { Huawei, H3C, Cisco, Ruijie, TPLink, Juniper, MikroTik, Ubiquiti, Other }
public enum DeviceRole { Switch, Router, Firewall }
public enum PortMode { Access, Trunk, Hybrid, Routed }
public enum AiProtocol { Responses, ClaudeMessages, Gemini, ChatCompletions, Ollama }
public enum ReadCommand { Version, Interfaces, Vlans, RunningConfiguration }

public sealed record Device(
    Guid Id, string Name, string ManagementIp, Vendor Vendor, string Model,
    string Firmware, DeviceRole Role, string Location, bool IsSimulation = true,
    int ManagementVlan = 1, int SshPort = 22, string? CredentialReference = null,
    string? HostFingerprint = null)
{
    [JsonIgnore] public string VendorLabel => Labels.Vendor(Vendor);
    [JsonIgnore] public string ModeLabel => IsSimulation ? "模拟设备" : "真实设备 · 只读";
    [JsonIgnore] public string RoleLabel => Role switch { DeviceRole.Router => "路由器", DeviceRole.Firewall => "防火墙", _ => "交换机" };
    public override string ToString() => Name;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80) throw new ArgumentException("设备名称必填，最多 80 字。");
        if (!IPAddress.TryParse(ManagementIp, out _)) throw new ArgumentException("请输入有效的管理 IP 地址。");
        if (ManagementVlan is < 1 or > 4094) throw new ArgumentException("管理 VLAN 必须为 1–4094。");
        if (SshPort is < 1 or > 65535) throw new ArgumentException("SSH 端口必须为 1–65535。");
        if (!Enum.IsDefined(Vendor) || !Enum.IsDefined(Role)) throw new ArgumentException("无效的设备厂商或角色。");
    }
}

public sealed record PortState(string Name, string Description, bool Enabled, bool LinkUp,
    int SpeedMbps, PortMode Mode, int Pvid, int[] TaggedVlans, bool IsUplink = false,
    Dictionary<string, string>? Extensions = null)
{
    [JsonIgnore] public string StatusLabel => !Enabled ? "已禁用" : LinkUp ? "已连接" : "未连接";
    [JsonIgnore] public string ModeLabel => Mode switch { PortMode.Access => "接入", PortMode.Trunk => "干道", PortMode.Hybrid => "混合", _ => "三层" };
    [JsonIgnore] public string TaggedLabel => string.Join(", ", TaggedVlans);
    [JsonIgnore] public string UplinkLabel => IsUplink ? "上联端口" : "接入端口";
}
public sealed record VlanState(int Id, string Name)
{
    public override string ToString() => $"VLAN {Id} · {Name}";
}
public sealed record DeviceState(Guid DeviceId, int Revision, PortState[] Ports, VlanState[] Vlans);
public sealed record ChangeRequest(int? CreateVlanId = null, string? VlanName = null,
    string[]? Ports = null, PortMode? Mode = null, int? Pvid = null,
    int[]? TaggedVlans = null, bool? Enabled = null, string? Description = null);
public sealed record PlanDiff(string Target, string Before, string After)
{
    public override string ToString() => $"{Target}：{Before} → {After}";
}
public sealed record ChangePlan(Guid Id, Guid DeviceId, string BaselineHash, ChangeRequest Request,
    string[] Commands, PlanDiff[] Differences, string[] Risks, DateTimeOffset CreatedAt);
public sealed record Snapshot(Guid Id, Guid DeviceId, DateTimeOffset CreatedAt, string Reason, string Content)
{
    [JsonIgnore] public string TimeLabel => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public override string ToString() => $"{TimeLabel} · {Reason}";
}
public sealed record AuditEntry(Guid Id, Guid DeviceId, DateTimeOffset CreatedAt, string Action, string Result)
{
    [JsonIgnore] public string TimeLabel => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
    public override string ToString() => $"{TimeLabel} · {Action}";
}
public sealed record AiProfile(Guid Id, string Name, AiProtocol Protocol, string BaseUrl, string Model,
    string? CredentialReference, int TimeoutSeconds = 60)
{
    public override string ToString() => Name;
}
public sealed record DriverDescriptor(Vendor Vendor, string Family, string Status, string Scope)
{
    public string VendorLabel => Labels.Vendor(Vendor);
}
public sealed record DeviceCredential(string Username, string Password);

public static class Labels
{
    public static string Vendor(Vendor vendor) => vendor switch
    {
        Domain.Vendor.Huawei => "华为",
        Domain.Vendor.H3C => "新华三",
        Domain.Vendor.Cisco => "思科",
        Domain.Vendor.Ruijie => "锐捷",
        Domain.Vendor.TPLink => "普联",
        Domain.Vendor.Juniper => "瞻博",
        Domain.Vendor.MikroTik => "MikroTik",
        Domain.Vendor.Ubiquiti => "优倍快",
        _ => "其他品牌"
    };
}
