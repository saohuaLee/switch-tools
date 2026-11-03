using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetOps.Domain;

namespace NetOps.Application;

/// <summary>只有确定性的统一模型可以进入变更引擎；本阶段执行入口仅允许模拟设备。</summary>
public sealed partial class ChangeEngine(IWorkspace workspace, IDriverRegistry drivers)
{
    private readonly Dictionary<Guid, ChangePlan> pending = [];
    public static string Hash(DeviceState state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state))));

    public ChangePlan Preview(Device device, ChangeRequest request)
    {
        device.Validate();
        if (!device.IsSimulation) throw new InvalidOperationException("真实设备配置尚未通过实验室验证，本版本仅开放只读连接与备份。");
        var state = workspace.GetState(device.Id) ?? throw new InvalidOperationException("设备状态不存在，请刷新。");
        // 深拷贝请求，避免调用者在预览后修改数组。
        request = JsonSerializer.Deserialize<ChangeRequest>(JsonSerializer.Serialize(request))!;
        Validate(device, state, request);
        var after = Transform(state, request);
        var diffs = Differences(state, after);
        if (diffs.Length == 0) throw new ArgumentException("当前配置已符合目标，没有需要执行的变更。");
        List<string> risks = ["此计划只改变本地模拟设备，不会连接真实网络设备。"];
        if (request.Ports?.Any(n => state.Ports.Any(p => p.Name == n && p.IsUplink)) == true)
            risks.Add("包含上联端口：真实设备上可能中断下游网络与管理连接。");
        if (request.Pvid == device.ManagementVlan || request.Ports?.Any(n => state.Ports.Any(p => p.Name == n && p.Pvid == device.ManagementVlan)) == true)
            risks.Add("涉及管理 VLAN：真实执行前必须核对管理路径并准备带外恢复。");
        if (request.Enabled == false) risks.Add("将禁用所选端口，其连接的终端可能离线。");
        var plan = new ChangePlan(Guid.NewGuid(), device.Id, Hash(state), request,
            drivers.Resolve(device).Compile(request), diffs, risks.ToArray(), DateTimeOffset.UtcNow);
        pending.Clear(); // 同一引擎只保留最新预览，旧页面的确认不能复用。
        pending[plan.Id] = plan;
        return Clone(plan);
    }

    public void ApplySimulation(Guid planId, bool confirmed)
    {
        if (!confirmed) throw new InvalidOperationException("请先确认已核对变更内容和风险。");
        if (!pending.Remove(planId, out var plan)) throw new InvalidOperationException("计划不存在、已执行或已失效，请重新预览。");
        if (DateTimeOffset.UtcNow - plan.CreatedAt > TimeSpan.FromMinutes(10)) throw new InvalidOperationException("计划已超过 10 分钟，请重新预览。");
        var device = workspace.GetDevices().Single(d => d.Id == plan.DeviceId);
        if (!device.IsSimulation) throw new InvalidOperationException("禁止向真实设备执行此模拟计划。");
        var before = workspace.GetState(device.Id) ?? throw new InvalidOperationException("设备状态已丢失。");
        if (Hash(before) != plan.BaselineHash) throw new InvalidOperationException("配置基线已经变化，请重新预览。");
        var after = Transform(before, plan.Request);
        Validate(device, before, plan.Request);
        workspace.CommitSimulation(before, after,
            new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "模拟变更前自动备份", JsonSerializer.Serialize(before, JsonOptions)),
            new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "模拟配置变更", $"验证通过，共 {plan.Differences.Length} 项差异；计划 {plan.Id}"));
        if (Hash(workspace.GetState(device.Id)!) != Hash(after)) throw new InvalidOperationException("变更后的状态验证失败。");
    }

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static ChangePlan Clone(ChangePlan plan) => JsonSerializer.Deserialize<ChangePlan>(JsonSerializer.Serialize(plan))!;
    private static void Validate(Device device, DeviceState state, ChangeRequest request)
    {
        if (device.Role != DeviceRole.Switch) throw new ArgumentException("本阶段端口/VLAN 配置仅用于交换机，路由器和防火墙适配待开发。");
        if (request.CreateVlanId is int vlan)
        {
            CheckVlan(vlan);
            if (state.Vlans.Any(v => v.Id == vlan)) throw new ArgumentException("此 VLAN 已存在，请选择其他编号。");
            if (string.IsNullOrWhiteSpace(request.VlanName) || !SafeName().IsMatch(request.VlanName))
                throw new ArgumentException("VLAN 名称使用 1–32 位英文字母、数字、下划线或短横线。");
        }
        else if (request.VlanName is not null) throw new ArgumentException("缺少新建 VLAN 编号。");
        if (request.Ports?.Length > 0)
        {
            if (request.Ports.Distinct().Count() != request.Ports.Length) throw new ArgumentException("端口不能重复。");
            if (request.Ports.Any(n => !SafePort().IsMatch(n) || !state.Ports.Any(p => p.Name == n))) throw new ArgumentException("包含未知或无效端口。");
            if (request.Mode is not (PortMode.Access or PortMode.Trunk)) throw new ArgumentException("本阶段只支持接入或干道模式，混合/三层端口待适配。");
            if (request.Pvid is not int pvid) throw new ArgumentException("请选择端口 PVID。");
            CheckVlan(pvid);
            var vlans = state.Vlans.Select(v => v.Id).Append(request.CreateVlanId ?? -1).ToHashSet();
            if (!vlans.Contains(pvid)) throw new ArgumentException("PVID 对应的 VLAN 不存在，请先创建。");
            if (request.Mode == PortMode.Trunk && (request.TaggedVlans is null || request.TaggedVlans.Length == 0)) throw new ArgumentException("干道端口必须明确指定允许通过的 VLAN。");
            if (request.TaggedVlans?.Any(v => v is < 1 or > 4094 || !vlans.Contains(v)) == true) throw new ArgumentException("允许列表包含不存在或无效的 VLAN。");
            if (request.Mode == PortMode.Access && request.TaggedVlans?.Length > 0) throw new ArgumentException("接入端口不能携带带标签 VLAN 列表。");
            if (request.Mode == PortMode.Trunk && !request.TaggedVlans!.Contains(pvid)) throw new ArgumentException("干道允许列表必须包含 PVID。");
            if (request.Description is not null && !SafeDescription().IsMatch(request.Description)) throw new ArgumentException("端口描述使用 1–64 位英文字母、数字、空格、点、下划线或短横线。");
        }
        else if (request.CreateVlanId is null || request.Mode is not null || request.Pvid is not null || request.Enabled is not null || request.Description is not null || request.TaggedVlans is not null)
            throw new ArgumentException("请选择至少一个端口或填写新建 VLAN。");
    }

    private static void CheckVlan(int vlan) { if (vlan is < 1 or > 4094) throw new ArgumentException("VLAN 编号必须为 1–4094。"); }
    private static DeviceState Transform(DeviceState state, ChangeRequest request)
    {
        var vlans = request.CreateVlanId is int id ? state.Vlans.Append(new(id, request.VlanName!)).OrderBy(v => v.Id).ToArray() : state.Vlans;
        var ports = state.Ports.Select(p => request.Ports?.Contains(p.Name) == true ? p with
        { Mode = request.Mode!.Value, Pvid = request.Pvid!.Value, TaggedVlans = request.Mode == PortMode.Trunk ? request.TaggedVlans!.Distinct().Order().ToArray() : [], Enabled = request.Enabled ?? p.Enabled, Description = request.Description ?? p.Description } : p).ToArray();
        return new(state.DeviceId, state.Revision + 1, ports, vlans);
    }
    private static PlanDiff[] Differences(DeviceState before, DeviceState after)
    {
        List<PlanDiff> diffs = [];
        foreach (var vlan in after.Vlans.Where(v => !before.Vlans.Any(b => b.Id == v.Id))) diffs.Add(new($"VLAN {vlan.Id}", "不存在", vlan.Name));
        foreach (var port in after.Ports)
        {
            var old = before.Ports.Single(p => p.Name == port.Name);
            if (JsonSerializer.Serialize(old) != JsonSerializer.Serialize(port)) diffs.Add(new(port.Name, Describe(old), Describe(port)));
        }
        return diffs.ToArray();
    }
    private static string Describe(PortState p) => $"{p.ModeLabel} · PVID {p.Pvid} · {(p.Enabled ? "启用" : "禁用")} · 允许 [{p.TaggedLabel}] · {p.Description}";
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}$")] private static partial Regex SafeName();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9/.-]{0,63}$")] private static partial Regex SafePort();
    [GeneratedRegex(@"^[A-Za-z0-9 _.-]{1,64}$")] private static partial Regex SafeDescription();
}
