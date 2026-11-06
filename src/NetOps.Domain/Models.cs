using System.Net;
using System.Text.Json.Serialization;

namespace NetOps.Domain;

public enum Vendor { Huawei, H3C, Cisco, Ruijie, TPLink, Juniper, MikroTik, Ubiquiti, Other }
public enum DeviceRole { Switch, Router, Firewall }
public enum PortMode { Access, Trunk, Hybrid, Routed, Unknown }
public enum AiProtocol { Responses, ClaudeMessages, Gemini, ChatCompletions, Ollama }
public enum ReadCommand { Version, Interfaces, Vlans, RunningConfiguration }
public enum ConnectionProtocol { Ssh, Console, Telnet, Netconf, Restconf, HttpApi }
public enum DuplexMode { Auto, Full, Half }
public enum MacBindingKind { StaticForwarding, SecureSource }

public sealed record MacBinding(string Address, int VlanId, string PortName, MacBindingKind Kind = MacBindingKind.StaticForwarding)
{
    [JsonIgnore] public string KindLabel => Kind == MacBindingKind.StaticForwarding ? "静态转发" : "端口安全绑定";
}
public sealed record PortSecurityState(string PortName, bool Enabled = false, int Maximum = 1, string Violation = "shutdown", bool HasUnsupportedOptions = false);
public sealed record PortSecuritySettings(string PortName, int Maximum = 1, string Violation = "shutdown");
public sealed record StackMemberState(int Id, string Role, string Status, string? Model = null, string? Identity = null, int? PortCount = null, bool? Online = null);

public sealed record ConnectionProfile(Guid Id, string Name, ConnectionProtocol Protocol = ConnectionProtocol.Ssh,
    int Port = 22, string? CredentialReference = null, string? HostFingerprint = null,
    string SerialPort = "COM1", int BaudRate = 9600, int DataBits = 8, string Parity = "None",
    string StopBits = "One", string FlowControl = "None", string? Endpoint = null, bool AllowTelnet = false)
{
    public string Label => $"{Name} · {ProtocolLabel}";
    public string ProtocolLabel => Protocol switch { ConnectionProtocol.Console => "Console 串口", ConnectionProtocol.HttpApi => "厂商 HTTP API", _ => Protocol.ToString().ToUpperInvariant() };
    public override string ToString() => Label;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || !Enum.IsDefined(Protocol)) throw new ArgumentException("请填写连接名称并选择连接方式。");
        if (Port is < 1 or > 65535) throw new ArgumentException("连接端口必须为 1–65535。");
        if (Protocol == ConnectionProtocol.Console && (string.IsNullOrWhiteSpace(SerialPort) || BaudRate is < 300 or > 4000000 || DataBits is < 5 or > 8)) throw new ArgumentException("串口名称、波特率或数据位无效。");
        if (Protocol == ConnectionProtocol.Telnet && !AllowTelnet) throw new ArgumentException("使用 Telnet 前须明确允许明文连接。");
        if (Protocol is ConnectionProtocol.Restconf or ConnectionProtocol.HttpApi &&
            (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0))
            throw new ArgumentException("API 地址应为 HTTPS 完整地址，且不包含凭据、查询参数和片段；本机服务允许 HTTP。");
    }
}

public sealed record Device(
    Guid Id, string Name, string ManagementIp, Vendor Vendor, string Model,
    string Firmware, DeviceRole Role, string Location, bool IsSimulation = true,
    int ManagementVlan = 1, int SshPort = 22, string? CredentialReference = null,
    string? HostFingerprint = null, ConnectionProfile[]? Connections = null, bool Archived = false, Guid? PreferredConnectionId = null, string? DriverId = null)
{
    [JsonIgnore] public string VendorLabel => Labels.Vendor(Vendor);
    [JsonIgnore] public string ModeLabel => IsSimulation ? "模拟设备" : "真实设备";
    [JsonIgnore] public string RoleLabel => Role switch { DeviceRole.Router => "路由器", DeviceRole.Firewall => "防火墙", _ => "交换机" };
    public override string ToString() => Name;
    public ConnectionProfile[] GetConnections() => Connections is { Length: > 0 } ? Connections : [new(Id, "默认 SSH", ConnectionProtocol.Ssh, SshPort, CredentialReference, HostFingerprint)];
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80) throw new ArgumentException("设备名称必填，最多 80 字。");
        if (!IPAddress.TryParse(ManagementIp, out _) && !(string.IsNullOrWhiteSpace(ManagementIp) && GetConnections().All(p => p.Protocol == ConnectionProtocol.Console))) throw new ArgumentException("请输入有效的管理 IP 地址；纯 Console 设备可留空。");
        if (ManagementVlan is < 1 or > 4094) throw new ArgumentException("管理 VLAN 必须为 1–4094。");
        if (SshPort is < 1 or > 65535) throw new ArgumentException("SSH 端口必须为 1–65535。");
        if (!Enum.IsDefined(Vendor) || !Enum.IsDefined(Role)) throw new ArgumentException("无效的设备厂商或角色。");
    }
}

public sealed record PortState(string Name, string Description, bool Enabled, bool LinkUp,
    int SpeedMbps, PortMode Mode, int Pvid, int[] TaggedVlans, bool IsUplink = false,
    Dictionary<string, string>? Extensions = null)
{
    [JsonIgnore] public string StatusLabel => !Enabled ? "已禁用" : Extensions?.ContainsKey("source") == true ? "链路未采集" : LinkUp ? "已连接" : "未连接";
    [JsonIgnore] public string ModeLabel => Mode switch { PortMode.Access => "接入", PortMode.Trunk => "干道", PortMode.Hybrid => "混合", PortMode.Routed => "三层", _ => "未识别" };
    [JsonIgnore] public string TaggedLabel => TaggedVlans.Length == 4094 ? "全部 VLAN" : string.Join(", ", TaggedVlans);
    [JsonIgnore] public string UplinkLabel => IsUplink ? "上联端口" : "接入端口";
}
public sealed record VlanState(int Id, string Name)
{
    public override string ToString() => $"VLAN {Id} · {Name}";
}
public sealed record DeviceState(Guid DeviceId, int Revision, PortState[] Ports, VlanState[] Vlans, DateTimeOffset? CapturedAt = null,
    MacBinding[]? MacBindings = null, PortSecurityState[]? PortSecurity = null,
    StackMemberState[]? StackMembers = null, bool InventoryCollected = false, string? InventoryWarning = null);
public sealed record ChangeRequest(int? CreateVlanId = null, string? VlanName = null,
    string[]? Ports = null, PortMode? Mode = null, int? Pvid = null,
    int[]? TaggedVlans = null, bool? Enabled = null, string? Description = null,
    int? RenameVlanId = null, int? DeleteVlanId = null, MacBinding[]? AddMacBindings = null, MacBinding[]? RemoveMacBindings = null,
    PortSecuritySettings[]? SecuritySettings = null, bool MigratePortSecurity = false);
public sealed record PlanDiff(string Target, string Before, string After)
{
    public override string ToString() => $"{Target}：{Before} → {After}";
}
public sealed record ChangePlan(Guid Id, Guid DeviceId, string BaselineHash, ChangeRequest Request,
    string[] Commands, PlanDiff[] Differences, string[] Risks, DateTimeOffset CreatedAt,
    bool IsSimulation = true, string? TargetIdentityHash = null, ChangeRequest[]? Requests = null);
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
    string? CredentialReference, int TimeoutSeconds = 60, bool IsDefault = false)
{
    public override string ToString() => Name;
    [JsonIgnore] public string DisplayLabel => IsDefault ? $"{Name}（默认）" : Name;
}
public sealed record DriverDescriptor(Vendor Vendor, string Family, string Status, string Scope)
{
    public string VendorLabel => Labels.Vendor(Vendor);
}
public sealed record DeviceCredential(string Username, string Password, string? PrivateKey = null, string? KeyPassphrase = null)
{
    public override string ToString() => "受保护的设备凭据";
}

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
