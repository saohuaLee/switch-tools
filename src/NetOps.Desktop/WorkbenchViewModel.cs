using System.Collections.ObjectModel;
using System.Text.Json;
using NetOps.Application;
using NetOps.Domain;
using NetOps.Infrastructure;

namespace NetOps.Desktop;

public sealed record PendingChange(Guid Id, Guid DeviceId, string DeviceName, string Summary, ChangeRequest Request);
public sealed record MemberHealthRow(int Id, string Role, string Status, string Cpu, string Memory, string Temperature, string Source);
public sealed class MacDraftRow : Observable
{
    public MacBinding? OriginalBinding { get; init; }
    public string OldAddress => OriginalBinding?.Address ?? "新增绑定";
    private string address = "", vlan = "10", portName = "";
    public string Address { get => address; set => Set(ref address, value); }
    public string Vlan { get => vlan; set => Set(ref vlan, value); }
    public string PortName { get => portName; set => Set(ref portName, value); }
}
public sealed record DeviceHealthRow(Device Device, MonitorSample? Sample, MonitorProfile? Profile, int Members)
{
    public string Name => Device.Name;
    public string Address => Device.ManagementIp;
    public string Vendor => Device.VendorLabel;
    public string VendorModel => $"{Vendor} · {Device.Model}";
    public string Group => Device.Location;
    public string SourceLabel => Device.IsSimulation ? "模拟设备" : "真实设备";
    public string StatusLabel => Profile?.Maintenance == true ? "维护中" : Profile?.Enabled != true ? "未启用监控" : Sample?.Reachability switch { Reachability.Online => "在线", Reachability.Suspect => "疑似不可达", Reachability.Offline => "管理不可达", Reachability.Maintenance => "维护中", _ => "状态未知" };
    public string StatusColor => Profile?.Enabled != true ? "#64748B" : Sample?.Reachability switch { Reachability.Online => "#138A67", Reachability.Offline => "#DC475B", Reachability.Suspect => "#C88A19", _ => "#64748B" };
    public string Cpu => Highest("cpu"); public string Memory => Highest("memory"); public string Temperature => Highest("temperature");
    public string CollectionLabel => Profile?.Enabled != true ? "可在监控设置中启用" : Sample is null ? "等待首次采集" : Sample.Error ?? "采集正常";
    public string LastSeen => Sample?.CollectedAt.ToLocalTime().ToString("HH:mm:ss") ?? "—";
    public string Highest(string prefix)
    {
        if (Profile?.Enabled != true || Profile.Maintenance) return "—";
        var readings = Sample?.Metrics.Where(m => m.Key.StartsWith(prefix)).ToArray() ?? [];
        var valid = readings.Where(m => m.Quality == CollectionQuality.Valid && m.Value is not null).OrderByDescending(m => m.Value).FirstOrDefault();
        return valid?.ValueLabel ?? (readings.Any(m => m.Quality == CollectionQuality.Stale) ? "已过期" : "—");
    }
}
public sealed class TopologyNodeView(Guid id, string name, string subtitle, DeviceHealthRow? health, StackMemberState[] members, Guid? ownerId = null, int? memberId = null) : Observable
{
    public Guid Id { get; } = id; public string Name { get; } = name; public string Subtitle { get; } = subtitle;
    public DeviceHealthRow? Health { get; } = health; public StackMemberState[] Members { get; } = members;
    public Guid? OwnerId { get; } = ownerId; public int? MemberId { get; } = memberId;
    public bool IsKnown => Health is not null;
    private double x, y;
    public double X { get => x; set => Set(ref x, value); } public double Y { get => y; set => Set(ref y, value); }
}
public sealed partial class MainViewModel
{
    private LocalWorkspace? observability => workspace as LocalWorkspace;
    public ObservableCollection<DeviceHealthRow> DeviceRows { get; } = [];
    public IEnumerable<DeviceHealthRow> FilteredDeviceRows => DeviceRows.Where(r => $"{r.Name} {r.Address} {r.Vendor} {r.Device.Model} {r.Group}".Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) && (DeviceGroup == "全部分组" || r.Group == DeviceGroup));
    private string deviceGroup = "全部分组";
    public string DeviceGroup { get => deviceGroup; set { if (value is not null && Set(ref deviceGroup, value)) NotifyDeviceFilter(); } }
    private string[] deviceGroups = ["全部分组"];
    public string[] DeviceGroups => deviceGroups;
    public string DeviceCountLabel => $"显示 {FilteredDeviceRows.Count()} / {DeviceRows.Count} 台";
    public bool HasFilteredDevices => FilteredDeviceRows.Any();
    private void NotifyDeviceFilter() { Changed(nameof(FilteredDeviceRows)); Changed(nameof(DeviceCountLabel)); Changed(nameof(HasFilteredDevices)); }
    public ObservableCollection<AlarmEvent> AlarmRows { get; } = [];
    public ObservableCollection<PendingChange> PendingChanges { get; } = [];
    public ObservableCollection<MacDraftRow> MacRows { get; } = [];
    private readonly Dictionary<Guid, (string Hash, string Identity)> queueBaselines = [];
    private readonly Dictionary<Guid, (string Address, string Vlan, string Port, MacBinding? Original)[]> macRowDrafts = [];
    private readonly Dictionary<Guid, (bool Auto, string Maximum, string Violation, MacBindingKind Kind, string PastePort, string PasteText)> macSettingsDrafts = [];
    private void SaveMacRows(Guid? id)
    {
        if (id is not Guid key) return;
        macRowDrafts[key] = MacRows.Select(r => (r.Address, r.Vlan, r.PortName, r.OriginalBinding)).ToArray();
        macSettingsDrafts[key] = (MacAutomaticMaximum, macMaximum, SelectedMacViolation.Value, SelectedMacKind.Value, MacPastePort, MacPasteText);
    }
    private void RestoreMacRows(Guid? id)
    {
        MacRows.Clear();
        var saved = id is Guid key && macSettingsDrafts.TryGetValue(key, out var settings) ? settings : (Auto: true, Maximum: "1", Violation: "shutdown", Kind: SelectedDevice?.Vendor == Vendor.Cisco ? MacBindingKind.SecureSource : MacBindingKind.StaticForwarding, PastePort: "", PasteText: "");
        macAutomaticMaximum = saved.Auto; macMaximum = saved.Maximum; selectedMacViolation = MacViolations.Single(v => v.Value == saved.Violation);
        selectedMacKind = MacKinds.Single(k => k.Value == saved.Kind); macPastePort = saved.PastePort; macPasteText = saved.PasteText;
        if (id is Guid rowKey && macRowDrafts.TryGetValue(rowKey, out var rows)) foreach (var r in rows) { AddRow(r.Port, r.Vlan, r.Original); MacRows[^1].Address = r.Address; }
        foreach (var name in new[] { nameof(SelectedMacKind), nameof(IsSecureMac), nameof(MacAutomaticMaximum), nameof(MacManualMaximum), nameof(SelectedMacViolation), nameof(MacViolationHelp), nameof(MacPastePort), nameof(MacPasteText) }) Changed(name);
        MacEditorChanged();
    }
    private ActionCommand[] workbenchCommands = [];
    public ActionCommand Navigate { get; private set; } = null!;
    public ActionCommand OpenDevice { get; private set; } = null!;
    public ActionCommand BackToDevices { get; private set; } = null!;
    public ActionCommand ClearDeviceFilter { get; private set; } = null!;
    public ActionCommand OpenDeviceQueue { get; private set; } = null!;
    public ActionCommand StagePorts { get; private set; } = null!;
    public ActionCommand StageVlan { get; private set; } = null!;
    public ActionCommand StageMac { get; private set; } = null!;
    public ActionCommand StageDeleteMac { get; private set; } = null!;
    public ActionCommand EditSelectedMac { get; private set; } = null!;
    public ActionCommand FormatMacRows { get; private set; } = null!;
    public ActionCommand ImportMacRows { get; private set; } = null!;
    public ActionCommand ReviewQueue { get; private set; } = null!;
    public ActionCommand RemovePending { get; private set; } = null!;
    public ActionCommand ResetPortField { get; private set; } = null!;
    public ActionCommand AddMacRow { get; private set; } = null!;
    public ActionCommand RemoveMacRow { get; private set; } = null!;
    public ActionCommand AcknowledgeAlarm { get; private set; } = null!;
    public ActionCommand StartMonitoring { get; private set; } = null!;
    public ActionCommand StopMonitoring { get; private set; } = null!;
    public ActionCommand RefreshDashboard { get; private set; } = null!;
    public event Action? CollectorStartRequested;
    public event Action? TopologyChanged;
    private void InitializeWorkbench()
    {
        Navigate = new(p => { var target = int.Parse(p?.ToString() ?? "0"); if (target == 2) ReturnToDevices(); else Page = target; });
        BackToDevices = new(_ => ReturnToDevices(), () => IsDeviceWorkspace && !ReviewOpen);
        ClearDeviceFilter = new(_ => { Search = ""; DeviceGroup = "全部分组"; });
        OpenDeviceQueue = new(_ => { SelectedPending = PendingChanges.FirstOrDefault(p => p.DeviceId == SelectedDevice?.Id); Page = 4; }, () => DevicePendingCount > 0);
        OpenDevice = new(p => { if (p is DeviceHealthRow row) SelectedDevice = Devices.First(d => d.Id == row.Device.Id); else if (p is Guid id) SelectedDevice = Devices.First(d => d.Id == id); Page = 2; DeviceFocused = true; Section = 0; TaskLanding = true; LastResult = ""; }, () => SelectedDevice is not null && !IsApplying);
        StagePorts = new(_ => Guard(() => { Stage(BuildPortRequest(), $"端口配置 · {Ports.Count(p => p.Picked)} 个端口"); loading = true; ApplyMode = ApplyPvid = ApplyAllowed = ApplyEnabled = ApplyDescription = CreateInlineVlan = false; loading = false; FillCurrentValues(); EditorOpen = false; NotifyQueue(); }), () => CanPreviewPorts && Ready);
        StageVlan = new(_ => Guard(() => Stage(BuildVlanRequest(), VlanAction.Label + " · VLAN " + VlanId)), () => CanConfigure && Ready && VlanValidation.Length == 0);
        StageMac = new(_ => Guard(() => { NormalizeMacDrafts(); var r = BuildMacRequest(); Stage(r, $"{SelectedMacKind.Label} · 新增 {r.AddMacBindings?.Length ?? 0} / 移除 {r.RemoveMacBindings?.Length ?? 0}"); MacRows.Clear(); MacBatch = ""; NotifyQueue(); }), () => CanConfigure && Ready && MacValidation.Length == 0);
        StageDeleteMac = new(_ => Guard(() => Stage(BuildDeleteMacRequest(), "删除 MAC 绑定")), () => DeleteMac.CanExecute(null) && Ready);
        EditSelectedMac = new(_ => Guard(LoadSelectedMac), () => CanConfigure && Ready && MacBindings.Any(m => m.Picked));
        FormatMacRows = new(_ => Guard(NormalizeMacDrafts), () => MacRows.Count > 0 && Ready);
        ImportMacRows = new(_ => RunMacEditorAction(ImportMacDrafts), () => CanConfigure && Ready && !string.IsNullOrWhiteSpace(MacPasteText));
        ReviewQueue = new(_ => Guard(ShowQueue), () => CanConfigure && Ready && PendingChanges.Any(p => p.DeviceId == SelectedDevice?.Id) && QueueValidation.Length == 0);
        RemovePending = new(p => { if (p is PendingChange item) { PendingChanges.Remove(item); InvalidatePlan(); if (!PendingChanges.Any(c => c.DeviceId == item.DeviceId)) queueBaselines.Remove(item.DeviceId); NotifyQueue(); } }, () => !IsApplying);
        ResetPortField = new(p => { switch (p?.ToString()) { case "mode": ApplyMode = false; break; case "pvid": ApplyPvid = false; CreateInlineVlan = false; break; case "allowed": ApplyAllowed = false; break; case "enabled": ApplyEnabled = false; break; case "description": ApplyDescription = false; break; } FillCurrentValues(); DraftChanged(); });
        AddMacRow = new(_ => { var selected = Ports.Where(p => p.Picked).ToArray(); var projected = ProjectedState(); if (selected.Length == 0) AddRow("", "10"); else foreach (var port in selected) AddRow(port.State.Name, (projected?.Ports.FirstOrDefault(p => p.Name == port.State.Name)?.Pvid ?? port.State.Pvid).ToString()); });
        RemoveMacRow = new(p => { if (p is MacDraftRow row) MacRows.Remove(row); NotifyQueue(); });
        AcknowledgeAlarm = new(p => { if (p is AlarmEvent alarm && observability is not null) { observability.SaveAlarm(alarm with { Acknowledged = true }); RefreshObservability(); } });
        StartMonitoring = new(_ => { observability?.RequestCollectorStop(false); CollectorStartRequested?.Invoke(); RefreshObservability(); });
        StopMonitoring = new(_ => { observability?.RequestCollectorStop(true); Status = "已请求停止后台采集，当前结果会保留并在过期后标记。"; });
        RefreshDashboard = new(_ => RefreshObservability());
        workbenchCommands = [Navigate, OpenDevice, BackToDevices, ClearDeviceFilter, OpenDeviceQueue, StagePorts, StageVlan, StageMac, StageDeleteMac, EditSelectedMac, FormatMacRows, ImportMacRows, ReviewQueue, RemovePending, ResetPortField, AddMacRow, RemoveMacRow, AcknowledgeAlarm, StartMonitoring, StopMonitoring, RefreshDashboard];
        MacRows.CollectionChanged += (_, _) => MacEditorChanged();
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SelectedDevice)) { foreach (var name in new[] { nameof(PageTitle), nameof(PageSubtitle), nameof(SelectedDeviceMetrics), nameof(SelectedHistory), nameof(CurrentHealth), nameof(MetricsHint), nameof(MemberHealthRows), nameof(HasMemberHealth), nameof(QueueValidation), nameof(AvailableVlans), nameof(DevicePendingCount), nameof(DeviceQueueLabel), nameof(NextStepHint) }) Changed(name); } };
        RefreshObservability();
        InitializeGuidedWorkflow();
    }
    private void AddRow(string port, string vlan, MacBinding? original = null)
    { var row = new MacDraftRow { PortName = port, Vlan = vlan, OriginalBinding = original }; row.PropertyChanged += (_, _) => MacEditorChanged(); MacRows.Add(row); }
    private int page = 2;
    public int Page { get => page; set { if (Set(ref page, value)) { EditorOpen = false; AiOpen = false; Changed(nameof(PageTitle)); Changed(nameof(PageSubtitle)); Changed(nameof(IsDeviceWorkspace)); RefreshWorkbenchCommands(); } } }
    public string PageTitle => Page switch { 1 => "网络拓扑", 2 => DeviceFocused ? SelectedDevice?.Name ?? "设备工作区" : "设备中心", 3 => "告警与事件", 4 => "批量草稿", 5 => "系统设置", _ => "网络总览" };
    public string PageSubtitle => Page switch { 1 => "从连接关系定位设备与端口", 2 => DeviceFocused ? "设备中心 / 设备工作区" : "选择设备 → 选择操作 → 填写配置 → 审核执行", 3 => "持续异常、恢复时间和处理记录", 4 => "可选功能：把同一设备的多项配置一起审核和执行", 5 => "管理采集器、AI 服务和驱动能力", _ => "网络状态、资源趋势和需要处理的事项" };
    private bool deviceFocused;
    public bool DeviceFocused { get => deviceFocused; set { Set(ref deviceFocused, value); Changed(nameof(PageTitle)); Changed(nameof(PageSubtitle)); Changed(nameof(IsDeviceWorkspace)); RefreshWorkbenchCommands(); } }
    public bool IsDeviceWorkspace => Page == 2 && DeviceFocused;
    private void ReturnToDevices() { EditorOpen = false; AiOpen = false; DeviceFocused = false; Page = 2; }
    private DeviceHealthRow? selectedRow;
    public DeviceHealthRow? SelectedRow { get => selectedRow; set { if (Set(ref selectedRow, value) && value is not null && value.Device.Id != SelectedDevice?.Id) SelectedDevice = Devices.First(d => d.Id == value.Device.Id); } }
    public string[] PortNames => Ports.Select(p => p.State.Name).ToArray();
    public VlanState[] AvailableVlans => ProjectedState()?.Vlans ?? Vlans.ToArray();
    public string ConnectionStage => Busy ? Operation : CanConfigure ? "配置基线已就绪" : SelectedDevice?.IsSimulation == true ? "模拟设备 · 等待读取" : "尚未建立配置基线";
    public string QueueLabel => $"批量草稿 {PendingChanges.Count} 组";
    public bool HasPendingChanges => PendingChanges.Count > 0;
    public int DevicePendingCount => PendingChanges.Count(p => p.DeviceId == SelectedDevice?.Id);
    public string DeviceQueueLabel => $"审核本机变更（{DevicePendingCount} 组） →";
    public string ReadCurrentLabel => CanConfigure ? "重新读取配置" : "读取设备配置";
    public string NextStepHint => Busy ? Operation : !SupportsConfiguration ? CapabilityHint : !CanConfigure ? "先选择要做的事；修改配置前点击“读取设备配置”。" : TaskLanding ? "配置已读取，请在下方选择要完成的操作。" : "填写完成后，点击当前页“下一步：审核并执行”。";
    public bool HasMemberHealth => MemberHealthRows.Length > 0;
    public string QueueValidation
    {
        get
        {
            if (SelectedDevice is null || !PendingChanges.Any(p => p.DeviceId == SelectedDevice.Id)) return "请选中需要审核的设备变更。";
            try { ChangeEngine.ProjectBatch(SelectedDevice, workspace.GetState(SelectedDevice.Id)!, PendingChanges.Where(p => p.DeviceId == SelectedDevice.Id).Select(p => p.Request)); return ""; }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { return "变更依赖未满足：" + ex.Message; }
        }
    }
    private PendingChange? selectedPending;
    public PendingChange? SelectedPending { get => selectedPending; set { if (Set(ref selectedPending, value) && value is not null && value.DeviceId != SelectedDevice?.Id) SelectedDevice = Devices.First(d => d.Id == value.DeviceId); } }
    public int TotalCount => DeviceRows.Count;
    public int OnlineCount => DeviceRows.Count(r => r.Profile?.Enabled == true && r.Sample?.Reachability == Reachability.Online);
    public int OfflineCount => DeviceRows.Count(r => r.Profile?.Enabled == true && r.Sample?.Reachability is Reachability.Offline or Reachability.Suspect);
    public int AlarmCount => AlarmRows.Count(a => a.RecoveredAt is null);
    public DeviceHealthRow? CurrentHealth => DeviceRows.FirstOrDefault(r => r.Device.Id == SelectedDevice?.Id);
    public MetricReading[] SelectedDeviceMetrics => CurrentHealth?.Sample?.Metrics ?? [];
    public MonitorSample[] SelectedHistory => SelectedDevice is null ? [] : observability?.Samples(SelectedDevice.Id, 1440).OrderBy(s => s.CollectedAt).ToArray() ?? [];
    public string MetricsHint => CurrentHealth is null ? "请选择设备查看监控指标" : $"{CurrentHealth.SourceLabel} · {CurrentHealth.CollectionLabel} · 最后采集 {CurrentHealth.LastSeen}";
    public MemberHealthRow[] MemberHealthRows
    {
        get
        {
            var sample = CurrentHealth?.Sample; var known = StackMembers.Select(m => m.Id).Union(sample?.Members?.Select(m => m.Id) ?? []).Order();
            var fresh = sample?.MembersCapturedAt is DateTimeOffset time && DateTimeOffset.UtcNow - time < TimeSpan.FromMinutes(10) && sample.Reachability == Reachability.Online;
            return known.Select(id => { var member = sample?.Members?.FirstOrDefault(m => m.Id == id); string Value(string prefix) => sample?.Metrics.FirstOrDefault(m => m.MemberId == id && m.Key.StartsWith(prefix))?.ValueLabel ?? "未提供"; return new MemberHealthRow(id, member?.Role ?? StackMembers.FirstOrDefault(m => m.Id == id)?.Role ?? "未知", fresh && member is not null ? member.Status : "未监测 / 已过期", Value("cpu"), Value("memory"), Value("temperature"), fresh ? "监控采集" : "仅有配置读取快照"); }).ToArray();
        }
    }
    public string CollectorLabel { get; private set; } = "采集器未启动";
    public string OverviewHint => DeviceRows.Any(r => r.Device.IsSimulation) ? "包含明确标识的模拟设备；真实设备需配置监控后启用采集。" : "指标来源、采集时间和异常状态分别记录。";
    public TopologyNodeView[] TopologyNodes { get; private set; } = [];
    public TopologyLink[] TopologyEdges { get; private set; } = [];
    private readonly HashSet<Guid> expandedStacks = [];
    private string topologySearch = "", topologyGroup = "全部分组";
    public string TopologySearch { get => topologySearch; set { Set(ref topologySearch, value); RefreshTopology(); } }
    public string TopologyGroup { get => topologyGroup; set { if (value is not null) { Set(ref topologyGroup, value); RefreshTopology(); } } }
    private string[] topologyGroups = ["全部分组"];
    public string[] TopologyGroups => topologyGroups;
    public void ToggleStack() { if (SelectedTopologyNode is not { Members.Length: > 1 } node) { Status = "请先选择堆叠设备。"; return; } if (!expandedStacks.Add(node.Id)) expandedStacks.Remove(node.Id); RefreshTopology(); }
    public TopologyNodeView? EndpointNode(Guid deviceId, string port)
    { var root = TopologyNodes.FirstOrDefault(n => n.Id == deviceId); if (root is null) return null; var member = SwitchInventory.MemberId(port, root.Members); return TopologyNodes.FirstOrDefault(n => n.OwnerId == deviceId && n.MemberId == member) ?? root; }
    private TopologyNodeView? selectedTopologyNode;
    public TopologyNodeView? SelectedTopologyNode { get => selectedTopologyNode; set { Set(ref selectedTopologyNode, value); Changed(nameof(TopologyNodeDetails)); Changed(nameof(CanExpandStack)); Changed(nameof(CanOpenTopologyDevice)); } }
    private TopologyLink? selectedTopologyLink;
    public TopologyLink? SelectedTopologyLink { get => selectedTopologyLink; set { Set(ref selectedTopologyLink, value); Changed(nameof(TopologyNodeDetails)); Changed(nameof(CanRemoveManualLink)); Changed(nameof(CanOpenTopologyDevice)); } }
    public bool CanExpandStack => SelectedTopologyNode?.Members.Length > 1;
    public bool CanRemoveManualLink => SelectedTopologyLink?.Manual == true;
    public bool CanOpenTopologyDevice => SelectedTopologyNode?.Health is not null || SelectedTopologyLink is not null;
    public string TopologyNodeDetails => SelectedTopologyLink is { } l ? $"连接详情\n\n{DeviceRows.FirstOrDefault(d => d.Device.Id == l.LocalDeviceId)?.Name}\n{l.LocalPort}\n↓\n{l.RemoteName}\n{l.RemotePort}\n\n来源：{l.Source}\n采集：{l.ObservedAt.ToLocalTime():MM-dd HH:mm:ss}\n\n邻居关系不代表实时链路运行状态。" : SelectedTopologyNode is not { } n ? "选择节点查看设备与成员信息；双击进入设备工作区。" : $"{n.Name}\n{n.Subtitle}\n{n.Health?.StatusLabel ?? "未纳管邻居"}\n\nCPU {n.Health?.Cpu ?? "—"}   内存 {n.Health?.Memory ?? "—"}\n温度 {n.Health?.Temperature ?? "—"}\n\n" + string.Join("\n", n.Members.Select(m => $"成员 {m.Id} · {m.Role} · {m.Status} · {m.PortCount?.ToString() ?? "未知"} 口"));
    private DeviceState? ProjectedState()
    {
        if (SelectedDevice is null || workspace.GetState(SelectedDevice.Id) is not { } state) return null;
        try { return ChangeEngine.ProjectBatch(SelectedDevice, state, PendingChanges.Where(p => p.DeviceId == SelectedDevice.Id).Select(p => p.Request)); } catch { return state; }
    }
    public void Stage(ChangeRequest request, string summary)
    {
        var device = RequireDevice(); if (!CanConfigure) throw new InvalidOperationException(CapabilityHint);
        var state = workspace.GetState(device.Id)!;
        var canonical = JsonSerializer.Deserialize<ChangeRequest>(JsonSerializer.Serialize(request))!;
        canonical = canonical with { AddMacBindings = canonical.AddMacBindings?.Select(MacRules.Canonical).ToArray(), RemoveMacBindings = canonical.RemoveMacBindings?.Select(MacRules.Canonical).ToArray() };
        var requests = PendingChanges.Where(p => p.DeviceId == device.Id).Select(p => p.Request).Append(canonical).ToArray();
        if (requests.Length > 100) throw new ArgumentException("同一设备最多累计 100 组修改。");
        var projectedBefore = ChangeEngine.ProjectBatch(device, state, requests.SkipLast(1));
        var projectedAfter = ChangeEngine.ProjectBatch(device, state, requests);
        if (ChangeEngine.ConfigurationHash(projectedBefore) == ChangeEngine.ConfigurationHash(projectedAfter)) throw new ArgumentException("配置已符合目标，没有需要加入的修改。");
        if (queueBaselines.TryGetValue(device.Id, out var baseline) && (baseline.Hash != ChangeEngine.Hash(state) || baseline.Identity != ChangeEngine.IdentityHash(device))) throw new InvalidOperationException("待提交变更的设备或基线已变化，请先移除旧变更并重新核对。");
        queueBaselines[device.Id] = (ChangeEngine.Hash(state), ChangeEngine.IdentityHash(device));
        PendingChanges.Add(new(Guid.NewGuid(), device.Id, device.Name, summary, canonical)); InvalidatePlan(); NotifyQueue(); Status = $"已加入待提交：{summary}。尚未下发，可继续编辑或统一审核。";
    }
    private void ShowQueue()
    {
        var device = RequireDevice(); var state = workspace.GetState(device.Id)!;
        if (!queueBaselines.TryGetValue(device.Id, out var baseline) || baseline.Hash != ChangeEngine.Hash(state) || baseline.Identity != ChangeEngine.IdentityHash(device)) throw new InvalidOperationException("待提交基线已变化，请移除旧变更并重新核对。");
        plan = engine.PreviewMany(device, PendingChanges.Where(p => p.DeviceId == device.Id).Select(p => p.Request).ToArray()); reviewIncludesCurrent = false; PlanConfirmed = false; LiveRiskAccepted = false;
        NotifyPlan();
        ReviewOpen = true; RefreshCommands();
    }
    private void NotifyQueue()
    { foreach (var name in new[] { nameof(QueueLabel), nameof(HasPendingChanges), nameof(DevicePendingCount), nameof(DeviceQueueLabel), nameof(NextStepHint), nameof(QueueValidation), nameof(AvailableVlans), nameof(PortValidation), nameof(VlanValidation), nameof(MacValidation), nameof(MacMaximum), nameof(MacCountSummary) }) Changed(name); RefreshWorkbenchCommands(); }
    private void RefreshWorkbenchCommands() { foreach (var command in workbenchCommands) command.Refresh(); }
    public void RefreshObservability()
    {
        if (observability is null) return;
        var now = DateTimeOffset.UtcNow; var profiles = observability.MonitorProfiles().ToDictionary(p => p.DeviceId); var samples = observability.LatestSamples().ToDictionary(s => s.DeviceId);
        var selectedId = SelectedDevice?.Id; DeviceRows.Clear();
        foreach (var device in workspace.GetDevices().Where(d => !d.Archived))
        {
            var profile = profiles.GetValueOrDefault(device.Id); var sample = samples.GetValueOrDefault(device.Id);
            if (sample is not null && profile is not null) sample = MonitorRules.Freshness(sample, profile, now);
            DeviceRows.Add(new(device, sample, profile, workspace.GetState(device.Id)?.StackMembers?.Length ?? 0));
        }
        SelectedRow = DeviceRows.FirstOrDefault(r => r.Device.Id == selectedId);
        var availableGroups = new[] { "全部分组" }.Concat(DeviceRows.Select(r => r.Group).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().Order()).ToArray();
        if (!availableGroups.SequenceEqual(deviceGroups)) { deviceGroups = availableGroups; Changed(nameof(DeviceGroups)); if (!deviceGroups.Contains(DeviceGroup)) deviceGroup = "全部分组"; Changed(nameof(DeviceGroup)); }
        NotifyDeviceFilter();
        var groups = DeviceRows.Select(r => string.IsNullOrWhiteSpace(r.Group) ? "未分组" : r.Group).Distinct().Order().Prepend("全部分组").ToArray();
        if (!groups.SequenceEqual(topologyGroups)) { topologyGroups = groups; Changed(nameof(TopologyGroups)); if (!groups.Contains(TopologyGroup)) topologyGroup = "全部分组"; Changed(nameof(TopologyGroup)); }
        AlarmRows.Clear(); foreach (var alarm in observability.Alarms()) AlarmRows.Add(alarm);
        var collector = observability.Collector(); CollectorLabel = observability.CollectorStopRequested() || collector?.StopRequested == true ? "采集器已停止" : collector is null ? "采集器未启动" : now - collector.Heartbeat > TimeSpan.FromSeconds(75) ? "采集器心跳已过期" : "后台采集运行中";
        RefreshTopology();
        foreach (var name in new[] { nameof(TotalCount), nameof(OnlineCount), nameof(OfflineCount), nameof(AlarmCount), nameof(CurrentHealth), nameof(SelectedDeviceMetrics), nameof(SelectedHistory), nameof(MetricsHint), nameof(MemberHealthRows), nameof(HasMemberHealth), nameof(CollectorLabel), nameof(OverviewHint) }) Changed(name);
    }
    public void RefreshTopology()
    {
        if (observability is null) return;
        var samples = observability.LatestSamples(); var devices = DeviceRows.Select(r => r.Device).ToArray(); var positions = observability.Positions().ToDictionary(p => p.DeviceId);
        var links = observability.TopologyLinks().Select(l => l with { RemoteDeviceId = l.RemoteDeviceId ?? TopologyDiscovery.Resolve(l.RemoteIdentity, l.RemoteName, devices, samples) }).ToArray();
        var matches = DeviceRows.Where(r => (TopologyGroup == "全部分组" || (string.IsNullOrWhiteSpace(r.Group) ? "未分组" : r.Group) == TopologyGroup) && $"{r.Name} {r.Address} {r.Vendor} {r.Group}".Contains(TopologySearch, StringComparison.OrdinalIgnoreCase)).Select(r => r.Device.Id).ToHashSet();
        links = links.Where(l => matches.Contains(l.LocalDeviceId) || l.RemoteDeviceId is Guid remote && matches.Contains(remote)).ToArray();
        var visible = matches.Concat(links.Select(l => l.LocalDeviceId)).Concat(links.Where(l => l.RemoteDeviceId is not null).Select(l => l.RemoteDeviceId!.Value)).ToHashSet();
        var nodes = DeviceRows.Where(r => visible.Contains(r.Device.Id)).Select(r =>
        {
            var metadata = workspace.GetState(r.Device.Id)?.StackMembers ?? [];
            var fresh = r.Sample?.MembersCapturedAt is DateTimeOffset time && DateTimeOffset.UtcNow - time < TimeSpan.FromMinutes(10) && r.Sample.Reachability == Reachability.Online;
            var members = metadata.Select(m => { var observed = fresh ? r.Sample!.Members?.FirstOrDefault(s => s.Id == m.Id) : null; return m with { Role = observed?.Role ?? m.Role, Status = observed?.Status ?? "未监测 / 已过期", Online = observed?.Online }; }).ToArray();
            return new TopologyNodeView(r.Device.Id, r.Name, r.Address + (r.Device.IsSimulation ? " · 模拟" : ""), r, members);
        }).ToList();
        foreach (var link in links.Where(l => l.RemoteDeviceId is null).DistinctBy(l => l.RemoteIdentity)) nodes.Add(new(new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(link.RemoteIdentity))[..16]), link.RemoteName, "未纳管邻居 · " + link.RemoteIdentity, null, []));
        for (var i = 0; i < nodes.Count; i++) { var pos = positions.GetValueOrDefault(nodes[i].Id); nodes[i].X = pos?.X ?? (i == 0 ? 450 : 130 + ((i - 1) % 4) * 330); nodes[i].Y = pos?.Y ?? (i == 0 ? 75 : 300 + ((i - 1) / 4) * 230); }
        foreach (var parent in nodes.Where(n => expandedStacks.Contains(n.Id) && n.Health is not null).ToArray())
        {
            var i = 0;
            foreach (var member in parent.Members)
            {
                var id = TopologyDiscovery.StableId(parent.Id, member.Id.ToString(), "member", "", "成员");
                var sample = parent.Health!.Sample; var memberFresh = sample?.MembersCapturedAt is DateTimeOffset time && DateTimeOffset.UtcNow - time < TimeSpan.FromMinutes(10) && sample.Reachability == Reachability.Online;
                var observed = memberFresh ? sample!.Members?.FirstOrDefault(m => m.Id == member.Id) : null;
                var health = parent.Health with { Members = 0, Sample = sample is null ? null : sample with { Reachability = observed?.Online is true ? Reachability.Online : observed?.Online is false ? Reachability.Offline : Reachability.Unknown, Metrics = sample.Metrics.Where(m => m.MemberId == member.Id).ToArray() } };
                var child = new TopologyNodeView(id, "成员 " + member.Id + " · " + member.Role, parent.Name, health, [], parent.Id, member.Id);
                var pos = positions.GetValueOrDefault(id); child.X = pos?.X ?? parent.X + (i % 3) * 300; child.Y = pos?.Y ?? parent.Y + 210 + i / 3 * 180; nodes.Add(child); i++;
            }
        }
        TopologyNodes = nodes.ToArray(); TopologyEdges = TopologyDiscovery.Edges(links);
        if (SelectedTopologyNode is { } selected) SelectedTopologyNode = TopologyNodes.FirstOrDefault(n => n.Id == selected.Id);
        if (SelectedTopologyLink is { } selectedLink) SelectedTopologyLink = TopologyEdges.FirstOrDefault(l => l.Id == selectedLink.Id);
        Changed(nameof(TopologyNodes)); Changed(nameof(TopologyEdges)); TopologyChanged?.Invoke();
    }
    public void MoveNode(TopologyNodeView node, double x, double y) { node.X = Math.Clamp(x, 20, 1800); node.Y = Math.Clamp(y, 20, 1800); observability?.SavePosition(new(node.Id, node.X, node.Y)); }
    public void AutoLayout() { for (var i = 0; i < TopologyNodes.Length; i++) MoveNode(TopologyNodes[i], i == 0 ? 450 : 130 + (i - 1) % 4 * 330, i == 0 ? 75 : 300 + (i - 1) / 4 * 230); TopologyChanged?.Invoke(); }
    public MonitorProfile CurrentMonitorProfile => observability?.MonitorProfiles().FirstOrDefault(p => p.DeviceId == SelectedDevice?.Id) ?? new(RequireDevice().Id);
    public MonitorCredential? CurrentMonitorCredential => CurrentMonitorProfile.CredentialReference is { } reference ? JsonSerializer.Deserialize<MonitorCredential>(vault.Read(reference)) : null;
    public void SaveMonitor(MonitorProfile profile, MonitorCredential? credential)
    {
        profile.Validate(); if (observability is null) throw new InvalidOperationException("当前工作区不支持监控。");
        var old = CurrentMonitorProfile; string? created = null;
        if (credential is not null) profile = profile with { CredentialReference = created = vault.Store(JsonSerializer.Serialize(credential)) };
        if (profile.Enabled && SelectedDevice?.IsSimulation == false && profile.Protocol != MonitorProtocol.Ssh && profile.CredentialReference is null) { if (created is not null) vault.Remove(created); throw new ArgumentException("启用真实设备监控前请填写监控凭据。"); }
        try { observability.SaveMonitorProfile(profile); } catch { if (created is not null) vault.Remove(created); throw; }
        if (profile.Enabled) { observability.RequestCollectorStop(false); CollectorStartRequested?.Invoke(); }
        RefreshObservability(); Status = "监控配置已保存。设备状态与指标采集质量会分别展示。";
    }
    public void SaveManualLink(TopologyLink link) { observability?.SaveTopologyLinks(link.LocalDeviceId, [link], false); RefreshTopology(); }
    public void RemoveManualLink() { if (SelectedTopologyLink is not { Manual: true } link) throw new InvalidOperationException("请先选择手工连接。"); observability?.RemoveManualLink(link.Id); SelectedTopologyLink = null; RefreshTopology(); }
    public string[] PortsForDevice(Guid id) => workspace.GetState(id)?.Ports.Select(p => p.Name).ToArray() ?? [];
}
