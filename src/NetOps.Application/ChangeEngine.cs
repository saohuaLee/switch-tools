using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetOps.Domain;

namespace NetOps.Application;

/// <summary>确定性的字段补丁、单次审核计划与基线校验；模拟和真实下发使用独立执行入口。</summary>
public sealed partial class ChangeEngine(IWorkspace workspace, IDriverRegistry drivers)
{
    private readonly Dictionary<Guid, ChangePlan> pending = [];
    public static string Hash(DeviceState state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state))));

    public ChangePlan Preview(Device device, ChangeRequest request)
    {
        device.Validate();
        var state = workspace.GetState(device.Id) ?? throw new InvalidOperationException("设备状态不存在，请刷新。");
        // 深拷贝请求，避免调用者在预览后修改数组。
        request = JsonSerializer.Deserialize<ChangeRequest>(JsonSerializer.Serialize(request))!;
        request = request with { AddMacBindings = request.AddMacBindings?.Select(MacRules.Canonical).ToArray(), RemoveMacBindings = request.RemoveMacBindings?.Select(MacRules.Canonical).ToArray() };
        Validate(device, state, request);
        var after = Transform(state, request);
        var diffs = Differences(state, after);
        if (diffs.Length == 0) throw new ArgumentException("当前配置已符合目标，没有需要执行的变更。");
        List<string> risks = [device.IsSimulation ? "此计划只改变本地模拟设备。" : "真实设备变更：执行前重新读取基线并加密备份；命令按实验性驱动生成，需人工审核。"];
        if (request.Ports?.Any(n => state.Ports.Any(p => p.Name == n && p.IsUplink)) == true)
            risks.Add("包含上联端口：真实设备上可能中断下游网络与管理连接。");
        if (request.Pvid == device.ManagementVlan || request.Ports?.Any(n => state.Ports.Any(p => p.Name == n && p.Pvid == device.ManagementVlan)) == true)
            risks.Add("涉及管理 VLAN：真实执行前必须核对管理路径并准备带外恢复。");
        if (request.Enabled == false) risks.Add("将禁用所选端口，其连接的终端可能离线。");
        if (MacRules.SecurityChanges(request).Length > 0) risks.Add("源 MAC 白名单会临时关闭相关端口后配置，完成后恢复原启停状态；不在白名单内的终端会被限制。最后一条删除后关闭该端口白名单。");
        if (request.AddMacBindings?.Any(b => b.Kind == MacBindingKind.StaticForwarding) == true) risks.Add("静态转发表指定目的 MAC 的出口，不等同于仅允许该源 MAC 接入的端口安全策略。");
        var plan = new ChangePlan(Guid.NewGuid(), device.Id, Hash(state), request,
            drivers.Resolve(device).Compile(request, state), diffs, risks.ToArray(), DateTimeOffset.UtcNow, device.IsSimulation, IdentityHash(device));
        pending.Clear(); // 同一引擎只保留最新预览，旧页面的确认不能复用。
        pending[plan.Id] = plan;
        return Clone(plan);
    }

    public void ApplySimulation(Guid planId, bool confirmed)
    {
        var plan = ConsumePlan(planId, confirmed);
        var device = workspace.GetDevices().Single(d => d.Id == plan.DeviceId);
        if (!device.IsSimulation || !plan.IsSimulation) throw new InvalidOperationException("禁止向真实设备执行模拟计划。");
        var before = workspace.GetState(device.Id) ?? throw new InvalidOperationException("设备状态已丢失。");
        var after = Transform(before, plan.Request);
        Validate(device, before, plan.Request);
        workspace.CommitSimulation(before, after,
            new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "模拟变更前自动备份", JsonSerializer.Serialize(before, JsonOptions)),
            new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "模拟配置变更", $"验证通过，共 {plan.Differences.Length} 项差异；计划 {plan.Id}"));
        if (Hash(workspace.GetState(device.Id)!) != Hash(after)) throw new InvalidOperationException("变更后的状态验证失败。");
    }

    public ChangePlan ConsumePlan(Guid planId, bool confirmed)
    {
        if (!confirmed) throw new InvalidOperationException("请先确认已核对变更内容和风险。");
        if (!pending.Remove(planId, out var plan)) throw new InvalidOperationException("计划不存在、已执行或已失效，请重新预览。");
        if (DateTimeOffset.UtcNow - plan.CreatedAt > TimeSpan.FromMinutes(10)) throw new InvalidOperationException("计划已超过 10 分钟，请重新预览。");
        var device = workspace.GetDevices().Single(d => d.Id == plan.DeviceId);
        if (device.IsSimulation != plan.IsSimulation || IdentityHash(device) != plan.TargetIdentityHash) throw new InvalidOperationException("设备资料或连接参数已经变化，请重新读取并预览。");
        var before = workspace.GetState(device.Id) ?? throw new InvalidOperationException("设备状态已丢失。");
        if (Hash(before) != plan.BaselineHash) throw new InvalidOperationException("配置基线已经变化，请重新预览。");
        return Clone(plan);
    }

    public void Invalidate() => pending.Clear();
    public static string IdentityHash(Device device) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(device))));
    public static string ConfigurationHash(DeviceState state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        state.DeviceId, Ports = state.Ports.OrderBy(p => p.Name).Select(p => new { p.Name, p.Description, p.Enabled, p.Mode, p.Pvid, TaggedVlans = p.TaggedVlans.Order().ToArray() }), Vlans = state.Vlans.OrderBy(v => v.Id), MacBindings = MacRules.Bindings(state), PortSecurity = MacRules.Security(state)
    }))));

    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static ChangePlan Clone(ChangePlan plan) => JsonSerializer.Deserialize<ChangePlan>(JsonSerializer.Serialize(plan))!;
    public static void ValidateRequest(Device device, DeviceState state, ChangeRequest request) => Validate(device, state, request);
    private static void Validate(Device device, DeviceState state, ChangeRequest request)
    {
        if (device.Role != DeviceRole.Switch) throw new ArgumentException("本阶段端口/VLAN 配置仅用于交换机，路由器和防火墙适配待开发。");
        if (new[] { request.CreateVlanId, request.RenameVlanId, request.DeleteVlanId }.Count(v => v is not null) > 1) throw new ArgumentException("一次计划只能新建、重命名或删除其中一个 VLAN。");
        if (request.CreateVlanId is int vlan)
        {
            CheckVlan(vlan);
            if (state.Vlans.Any(v => v.Id == vlan)) throw new ArgumentException("此 VLAN 已存在，请选择其他编号。");
            if (string.IsNullOrWhiteSpace(request.VlanName) || !SafeName().IsMatch(request.VlanName))
                throw new ArgumentException("VLAN 名称使用 1–32 位英文字母、数字、下划线或短横线。");
        }
        else if (request.RenameVlanId is int rename)
        {
            if (!state.Vlans.Any(v => v.Id == rename)) throw new ArgumentException("待修改的 VLAN 不存在。");
            if (string.IsNullOrWhiteSpace(request.VlanName) || !SafeName().IsMatch(request.VlanName)) throw new ArgumentException("VLAN 名称使用 1–32 位英文字母、数字、下划线或短横线。");
        }
        else if (request.VlanName is not null) throw new ArgumentException("缺少待修改的 VLAN 编号。");
        if (request.DeleteVlanId is int remove)
        {
            if (!state.Vlans.Any(v => v.Id == remove)) throw new ArgumentException("待删除的 VLAN 不存在。");
            if (remove == 1 || remove == device.ManagementVlan) throw new ArgumentException("不能删除默认 VLAN 或管理 VLAN。");
            if (state.Ports.Any(p => p.Pvid == remove || p.TaggedVlans.Contains(remove))) throw new ArgumentException("此 VLAN 仍被端口引用，请先调整成员关系。");
        }
        if (request.Ports?.Length > 0)
        {
            if (request.Ports.Distinct().Count() != request.Ports.Length) throw new ArgumentException("端口不能重复。");
            if (request.Ports.Any(n => !SafePort().IsMatch(n) || !state.Ports.Any(p => p.Name == n))) throw new ArgumentException("包含未知或无效端口。");
            if (request.Mode is not null and not (PortMode.Access or PortMode.Trunk)) throw new ArgumentException("此驱动只支持修改接入或干道模式。");
            if (request.Pvid is int pvid) CheckVlan(pvid);
            if (request.Mode is null && request.Pvid is null && request.TaggedVlans is null && request.Enabled is null && request.Description is null) throw new ArgumentException("请至少选择一个要修改的字段。");
            var vlans = state.Vlans.Select(v => v.Id).Append(request.CreateVlanId ?? -1).ToHashSet();
            if (request.TaggedVlans?.Any(v => v is < 1 or > 4094 || !vlans.Contains(v)) == true) throw new ArgumentException("允许列表包含不存在或无效的 VLAN。");
            foreach (var old in state.Ports.Where(p => request.Ports.Contains(p.Name)))
            {
                var mode = request.Mode ?? old.Mode;
                var targetPvid = request.Pvid ?? old.Pvid;
                if (request.Mode is not null || request.Pvid is not null || request.TaggedVlans is not null)
                {
                    if (mode is not (PortMode.Access or PortMode.Trunk)) throw new ArgumentException("混合/三层端口当前只允许修改描述与启停状态。");
                    if (!vlans.Contains(targetPvid)) throw new ArgumentException("PVID 对应的 VLAN 不存在，请先创建或勾选同步新建。");
                    var tagged = request.TaggedVlans ?? old.TaggedVlans;
                    if (mode == PortMode.Access && request.TaggedVlans?.Length > 0) throw new ArgumentException("接入端口不能携带带标签 VLAN 列表。");
                    if (mode == PortMode.Trunk && (tagged.Length == 0 || !tagged.Contains(targetPvid))) throw new ArgumentException("干道允许列表不能为空，且必须包含 PVID。");
                }
            }
            if (request.Description is not null && (Encoding.UTF8.GetByteCount(request.Description) > 128 || !SafeDescription().IsMatch(request.Description) || request.Description.IndexOfAny([';', '|']) >= 0))
                throw new ArgumentException("端口描述支持中文和常用标点，最多 128 个 UTF-8 字节；不能包含换行、控制字符、分号或竖线。留空表示清除。");
        }
        else if ((request.CreateVlanId is null && request.RenameVlanId is null && request.DeleteVlanId is null && request.AddMacBindings?.Length is not > 0 && request.RemoveMacBindings?.Length is not > 0) || request.Mode is not null || request.Pvid is not null || request.Enabled is not null || request.Description is not null || request.TaggedVlans is not null)
            throw new ArgumentException("请选择至少一个端口或填写新建 VLAN。");
        MacRules.Validate(device, state, request);
    }

    private static void CheckVlan(int vlan) { if (vlan is < 1 or > 4094) throw new ArgumentException("VLAN 编号必须为 1–4094。"); }
    public static DeviceState Transform(DeviceState state, ChangeRequest request)
        => TransformPortsAndVlans(state, request) with { MacBindings = MacRules.TargetBindings(state, request), PortSecurity = MacRules.TargetSecurity(state, request) };
    internal static DeviceState TransformPortsAndVlans(DeviceState state, ChangeRequest request)
    {
        var vlans = request.CreateVlanId is int id ? state.Vlans.Append(new(id, request.VlanName!)).OrderBy(v => v.Id).ToArray() : state.Vlans;
        if (request.RenameVlanId is int rename) vlans = vlans.Select(v => v.Id == rename ? v with { Name = request.VlanName! } : v).ToArray();
        if (request.DeleteVlanId is int remove) vlans = vlans.Where(v => v.Id != remove).ToArray();
        var ports = state.Ports.Select(p => request.Ports?.Contains(p.Name) == true ? p with
        { Mode = request.Mode ?? p.Mode, Pvid = request.Pvid ?? p.Pvid, TaggedVlans = request.Mode == PortMode.Access ? [] : request.TaggedVlans?.Distinct().Order().ToArray() ?? p.TaggedVlans, Enabled = request.Enabled ?? p.Enabled, Description = request.Description ?? p.Description } : p).ToArray();
        return state with { Revision = state.Revision + 1, Ports = ports, Vlans = vlans };
    }
    private static PlanDiff[] Differences(DeviceState before, DeviceState after)
    {
        List<PlanDiff> diffs = [];
        foreach (var b in MacRules.Bindings(before).Except(MacRules.Bindings(after))) diffs.Add(new($"{b.KindLabel} {b.Address} / VLAN {b.VlanId}", b.PortName, "删除"));
        foreach (var b in MacRules.Bindings(after).Except(MacRules.Bindings(before))) diffs.Add(new($"{b.KindLabel} {b.Address} / VLAN {b.VlanId}", "不存在", b.PortName));
        foreach (var name in MacRules.Security(before).Select(s => s.PortName).Union(MacRules.Security(after).Select(s => s.PortName)))
        {
            var old = MacRules.Security(before).SingleOrDefault(s => s.PortName == name); var next = MacRules.Security(after).SingleOrDefault(s => s.PortName == name);
            if (old != next) diffs.Add(new($"端口安全 {name}", old is null ? "未启用" : $"上限 {old.Maximum} / {old.Violation}", next is null ? "关闭白名单" : $"白名单 {next.Maximum} / restrict"));
        }
        foreach (var vlan in after.Vlans.Where(v => !before.Vlans.Any(b => b.Id == v.Id))) diffs.Add(new($"VLAN {vlan.Id}", "不存在", vlan.Name));
        foreach (var vlan in before.Vlans.Where(v => !after.Vlans.Any(a => a.Id == v.Id))) diffs.Add(new($"VLAN {vlan.Id}", vlan.Name, "删除"));
        foreach (var vlan in after.Vlans.Where(v => before.Vlans.Any(b => b.Id == v.Id && b.Name != v.Name))) diffs.Add(new($"VLAN {vlan.Id}", before.Vlans.Single(v => v.Id == vlan.Id).Name, vlan.Name));
        foreach (var port in after.Ports)
        {
            var old = before.Ports.Single(p => p.Name == port.Name);
            if (JsonSerializer.Serialize(old) != JsonSerializer.Serialize(port)) diffs.Add(new(port.Name, Describe(old), Describe(port)));
        }
        return diffs.ToArray();
    }
    private static string Describe(PortState p) => $"{p.ModeLabel} · PVID {p.Pvid} · {(p.Enabled ? "启用" : "禁用")} · 允许 [{p.TaggedLabel}] · {p.Description}";
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}$")] private static partial Regex SafeName();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9/:.-]{0,63}$")] private static partial Regex SafePort();
    [GeneratedRegex(@"\A[\p{L}\p{M}\p{N}\p{P}\p{S} ]*\z")] private static partial Regex SafeDescription();
}
