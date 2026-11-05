using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using NetOps.Application;
using NetOps.Domain;
using NetOps.Infrastructure;

namespace NetOps.Desktop;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
}
public sealed class ActionCommand(Action<object?> action, Func<bool>? enabled = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) action(parameter); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed record Choice<T>(T Value, string Label) { public override string ToString() => Label; }
public sealed class PortItem(PortState state, bool showFullName = false) : Observable
{
    public PortState State { get; } = state;
    private bool picked;
    public bool Picked { get => picked; set { if (Set(ref picked, value)) { Changed(nameof(Fill)); Changed(nameof(SelectionMark)); } } }
    public string Number => State.Name.Split('/').Last();
    public string TileLabel => showFullName ? State.Name.Replace("GigabitEthernet", "GE", StringComparison.Ordinal).Replace("FastEthernet", "FE", StringComparison.Ordinal) : Number;
    public string Fill => Picked ? "#DFEBFF" : "#F8FAFC";
    public string SelectionMark => Picked ? "✓" : "";
    public string Detail => $"{State.Name} · {State.ModeLabel} · PVID {State.Pvid} · {State.StatusLabel}";
    public override string ToString() => Detail;
}
public sealed class MembershipRow(PortState port)
{
    public string Name => port.Name;
    public string this[int id] => port.Pvid == id ? "PVID" : port.TaggedVlans.Contains(id) ? "允许" : "—";
}
public sealed record PortDraft(string[] Selected, bool ModeOn, PortMode Mode, bool PvidOn, string Pvid,
    bool VlansOn, string Vlans, bool EnabledOn, bool Enabled, bool DescriptionOn, string Description,
    bool CreateVlan, string NewVlanName);
public sealed class MacItem(MacBinding binding, PortSecurityState? security = null) : Observable
{
    public MacBinding Binding { get; } = binding;
    public PortSecurityState? Security { get; } = security;
    public string MaximumLabel => Binding.Kind == MacBindingKind.SecureSource ? Security?.Maximum.ToString() ?? "未读取" : "—";
    public string ViolationLabel => Binding.Kind == MacBindingKind.SecureSource && Security is not null ? ChangeEngine.ViolationDescription(Security.Violation) : "—";
    private bool picked;
    public bool Picked { get => picked; set => Set(ref picked, value); }
}
public sealed record PortPanelGroup(string Label, PortItem[] Items);

public sealed partial class MainViewModel : Observable, IDisposable
{
    private readonly IWorkspace workspace;
    private readonly ISecretVault vault;
    private readonly IDriverRegistry drivers;
    private readonly ChangeEngine engine;
    private readonly IAiClient ai;
    private readonly ISessionFactory sessions;
    private readonly NetworkOperations network;
    private readonly Dictionary<Guid, PortDraft> drafts = [];
    private readonly HashSet<Guid> prepared = [];
    private readonly Dictionary<Guid, Guid> preparedConnection = [];
    private readonly Dictionary<Guid, string> readResults = [];
    private readonly ActionCommand[] commands;
    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? terminalCancellation;
    private IDeviceSession? terminal;
    private Task? terminalPump;
    private bool loading;

    public MainViewModel(IWorkspace workspace, ISecretVault vault, IDriverRegistry drivers,
        IAiClient ai, ISessionFactory sessions, NetworkOperations network)
    {
        this.workspace = workspace; this.vault = vault; this.drivers = drivers; this.ai = ai; this.sessions = sessions; this.network = network;
        engine = new(workspace, drivers);
        Devices = new(workspace.GetDevices()); Profiles = new(workspace.GetAiProfiles());
        ReadCurrent = new(_ => _ = RunAsync("读取当前配置", ReadCurrentAsync), () => Ready && SelectedDevice is not null && !TerminalConnected);
        Backup = new(_ => _ = RunAsync("备份当前配置", BackupAsync), () => Ready && SelectedDevice is not null && !TerminalConnected);
        PreviewPorts = new(_ => Guard(() => ShowPlan(BuildPortRequest())), () => CanPreviewPorts);
        PreviewVlan = new(_ => Guard(() => ShowPlan(BuildVlanRequest())), () => CanConfigure && Ready && VlanValidation.Length == 0);
        ApplyPlan = new(_ => _ = RunAsync("执行变更", ApplyAsync), () => Ready && plan is not null && PlanConfirmed && (plan.IsSimulation || LiveRiskAccepted));
        CloseReview = new(_ => ReviewOpen = false, () => !IsApplying);
        TogglePort = new(p => { if (p is PortItem port) port.Picked = !port.Picked; }, () => CanConfigure);
        SelectAllPorts = new(_ => SelectPorts(p => !p.State.IsUplink && p.State.Mode == PortMode.Access), () => CanConfigure);
        ClearSelection = new(_ => SelectPorts(_ => false), () => Ports.Any(p => p.Picked));
        SelectRange = new(_ => Guard(() => { var names = PortSelections.Parse(PortRange, Ports.Select(p => p.State.Name).ToArray()); SelectPorts(p => names.Contains(p.State.Name)); }), () => CanConfigure);
        EditPorts = new(_ => { Section = 1; EditorOpen = true; }, () => CanConfigure && Ports.Any(p => p.Picked));
        ToggleAi = new(_ => AiOpen = !AiOpen);
        AskAi = new(_ => _ = RunAsync("AI 辅助分析", AskAiAsync), () => Ready && SelectedProfile is not null && ContextApproved && !string.IsNullOrWhiteSpace(AiQuestion));
        Cancel = new(_ => operationCancellation?.Cancel(), () => Busy);
        ConnectTerminal = new(_ => _ = RunAsync("连接终端", ConnectTerminalAsync), () => Ready && !TerminalConnected && SelectedDevice is not null && IsInteractiveProfile);
        DisconnectTerminal = new(_ => _ = DisconnectAsync(), () => TerminalConnected);
        SendTerminal = new(_ => _ = SendTerminalAsync(), () => TerminalConnected && TerminalInput.Length > 0);
        CompareSnapshot = new(_ => Guard(Compare), () => SelectedSnapshot is not null && SelectedDevice?.IsSimulation == true);
        NewVlan = new(_ => { SelectedVlan = null; VlanAction = VlanActions[0]; VlanId = Enumerable.Range(2, 4093).First(i => !AvailableVlans.Any(v => v.Id == i)).ToString(); VlanName = ""; });
        PreviewMac = new(_ => Guard(() => ShowPlan(BuildMacRequest())), () => CanConfigure && Ready && MacValidation.Length == 0);
        DeleteMac = new(_ => Guard(() => ShowPlan(BuildDeleteMacRequest())), () => CanConfigure && Ready && MacBindings.Any(b => b.Picked));
        MacTemplate = new(_ => MacBatch = string.Join("\n", Ports.Where(p => p.Picked).Select(p => $",{p.State.Pvid},{p.State.Name}")), () => CanConfigure && Ports.Any(p => p.Picked));
        SelectPortGroup = new(group => { if (group is PortPanelGroup g) { loading = true; foreach (var port in g.Items) port.Picked = true; loading = false; SelectionChanged(); } }, () => CanConfigure);
        commands = [ReadCurrent, Backup, PreviewPorts, PreviewVlan, ApplyPlan, CloseReview, TogglePort, SelectAllPorts, ClearSelection, SelectRange, EditPorts, ToggleAi, AskAi, Cancel, ConnectTerminal, DisconnectTerminal, SendTerminal, CompareSnapshot, NewVlan, PreviewMac, DeleteMac, MacTemplate, SelectPortGroup];
        SelectedProfile = Profiles.FirstOrDefault(p => p.IsDefault) ?? Profiles.FirstOrDefault(); EditingProfile = SelectedProfile; SelectedDevice = Devices.FirstOrDefault(d => !d.Archived);
        InitializeWorkbench();
    }

    public ObservableCollection<Device> Devices { get; }
    public ObservableCollection<PortItem> Ports { get; } = [];
    public ObservableCollection<VlanState> Vlans { get; } = [];
    public ObservableCollection<Snapshot> Snapshots { get; } = [];
    public ObservableCollection<AuditEntry> Audit { get; } = [];
    public ObservableCollection<AiProfile> Profiles { get; }
    public ObservableCollection<ConnectionProfile> Connections { get; } = [];
    public ObservableCollection<string> CommandHistory { get; } = [];
    public ObservableCollection<MacItem> MacBindings { get; } = [];
    public ActionCommand PreviewMac { get; }
    public ActionCommand DeleteMac { get; }
    public ActionCommand MacTemplate { get; }
    public ActionCommand SelectPortGroup { get; }
    public Choice<MacBindingKind>[] MacKinds { get; } = [new(MacBindingKind.StaticForwarding, "静态转发绑定"), new(MacBindingKind.SecureSource, "端口安全绑定（Cisco 接入口）")];
    private Choice<MacBindingKind>? selectedMacKind;
    public Choice<MacBindingKind> SelectedMacKind { get => selectedMacKind ?? MacKinds[SelectedDevice?.Vendor == Vendor.Cisco ? 1 : 0]; set { if (Set(ref selectedMacKind, value)) { Changed(nameof(IsSecureMac)); MacEditorChanged(); } } }
    private readonly Dictionary<Guid, string> macDrafts = [];
    private string macBatch = "";
    public string MacBatch { get => macBatch; set { Set(ref macBatch, value); if (SelectedDevice is not null) macDrafts[SelectedDevice.Id] = value; InvalidatePlan(); Changed(nameof(MacValidation)); RefreshCommands(); } }
    public string MacValidation { get { if (!CanConfigure) return CapabilityHint; try { ChangeEngine.ValidateRequest(RequireDevice(), ProjectedState() ?? workspace.GetState(RequireDevice().Id)!, BuildMacRequest()); return ""; } catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return ex.Message; } } }
    public Choice<PortMode>[] Modes { get; } = [new(PortMode.Access, "接入 Access"), new(PortMode.Trunk, "干道 Trunk")];
    public Choice<int>[] VlanActions { get; } = [new(0, "创建 VLAN"), new(1, "重命名 VLAN"), new(2, "删除 VLAN")];
    public Choice<AiProtocol>[] Protocols { get; } = [new(AiProtocol.Responses, "OpenAI 响应协议"), new(AiProtocol.ClaudeMessages, "Claude 消息协议"), new(AiProtocol.Gemini, "Gemini 内容生成协议"), new(AiProtocol.ChatCompletions, "对话补全兼容协议"), new(AiProtocol.Ollama, "Ollama 本机协议")];
    public IReadOnlyList<DriverDescriptor> DriverDescriptors => drivers.Descriptors;
    public MembershipRow[] MembershipRows => Ports.Select(p => new MembershipRow(p.State)).ToArray();
    public StackMemberState[] StackMembers => SelectedDevice is null ? [] : workspace.GetState(SelectedDevice.Id)?.StackMembers ?? [];
    public string InventorySummary
    {
        get
        {
            var state = SelectedDevice is null ? null : workspace.GetState(SelectedDevice.Id);
            if (state is null) return "尚未读取接口清单与堆叠成员。";
            if (SelectedDevice?.IsSimulation == true) return $"模拟数据 · {(StackMembers.Length > 1 ? $"{StackMembers.Length} 成员堆叠" : "单机示例")} · {Ports.Count} 个端口";
            return $"{(StackMembers.Length > 0 ? $"读取到 {StackMembers.Length} 个成员" : "堆叠状态未识别")} · {(state.InventoryCollected ? $"接口清单 {Ports.Count} 个物理端口" : $"配置中 {Ports.Count} 个端口，总数未确认")}\n{state.InventoryWarning}".Trim();
        }
    }
    public PortPanelGroup[] PortGroups => Ports.GroupBy(p => SwitchInventory.MemberId(p.State.Name, StackMembers)).Select(g => new PortPanelGroup(g.Key is int id ? $"成员 {id} · {StackMembers.Single(s => s.Id == id).Role} · {g.Count()} 个已采集端口" : "物理接口 · " + g.Count() + " 个", g.ToArray())).ToArray();
    public ActionCommand ReadCurrent { get; }
    public ActionCommand Backup { get; }
    public ActionCommand PreviewPorts { get; }
    public ActionCommand PreviewVlan { get; }
    public ActionCommand ApplyPlan { get; }
    public ActionCommand CloseReview { get; }
    public ActionCommand TogglePort { get; }
    public ActionCommand SelectAllPorts { get; }
    public ActionCommand ClearSelection { get; }
    public ActionCommand SelectRange { get; }
    public ActionCommand EditPorts { get; }
    public ActionCommand ToggleAi { get; }
    public ActionCommand AskAi { get; }
    public ActionCommand Cancel { get; }
    public ActionCommand ConnectTerminal { get; }
    public ActionCommand DisconnectTerminal { get; }
    public ActionCommand SendTerminal { get; }
    public ActionCommand CompareSnapshot { get; }
    public ActionCommand NewVlan { get; }

    private Device? selectedDevice;
    public Device? SelectedDevice
    {
        get => selectedDevice;
        set
        {
            if (value?.Id == selectedDevice?.Id && value == selectedDevice) return;
            if (TerminalConnected) { Status = "请先断开当前设备终端，再切换设备。"; Changed(); return; }
            SaveDraft(); SaveMacRows(selectedDevice?.Id); selectedDevice = value; Changed(); InvalidatePlan(); Connections.Clear();
            macBatch = value is null ? "" : macDrafts.GetValueOrDefault(value.Id, ""); Changed(nameof(MacBatch));
            foreach (var profile in value?.GetConnections() ?? []) Connections.Add(profile);
            selectedConnection = Connections.FirstOrDefault(p => p.Id == value?.PreferredConnectionId) ?? Connections.FirstOrDefault(); Changed(nameof(SelectedConnection));
            SelectedVlan = null; ContextApproved = false; ConnectionOutput = value is null ? "请选择设备。" : readResults.GetValueOrDefault(value.Id, "尚未连接与读取当前设备。点击“读取设备配置”开始。");
            RefreshCurrent(false); RestoreDraft(); RestoreMacRows(value?.Id); RefreshAll();
        }
    }
    private ConnectionProfile? selectedConnection;
    public ConnectionProfile? SelectedConnection
    {
        get => selectedConnection;
        set { if (TerminalConnected) { Status = "请先断开终端，再切换连接。"; Changed(); return; } if (Set(ref selectedConnection, value)) { InvalidatePlan(); if (SelectedDevice is not null) prepared.Remove(SelectedDevice.Id); RefreshAll(); } }
    }
    private int section;
    public int Section { get => section; set { if (Set(ref section, value)) { TaskLanding = false; if (value != 1) EditorOpen = false; NotifyTask(); } } }
    private string search = "";
    public string Search { get => search; set { Set(ref search, value); Changed(nameof(FilteredDevices)); NotifyDeviceFilter(); } }
    public IEnumerable<Device> FilteredDevices => Devices.Where(d => !d.Archived && $"{d.Name} {d.ManagementIp} {d.VendorLabel} {d.Location}".Contains(Search, StringComparison.OrdinalIgnoreCase));
    public string DeviceSummary => SelectedDevice is null ? "请选择设备" : $"{SelectedDevice.VendorLabel} · {SelectedDevice.Model} · {SelectedDevice.ManagementIp} · {SelectedDevice.Location}";
    public string CapabilityHint => SelectedDevice is null ? "请先选择设备。" : SelectedDevice.Role != DeviceRole.Switch ? "此角色支持连接与高级终端；三层和防火墙图形化配置按原方案列入 P1。" : SelectedDevice.Vendor is not (Vendor.Huawei or Vendor.H3C or Vendor.Cisco or Vendor.Ruijie) ? "此厂商当前提供通用会话接入，图形化配置驱动待适配。" : !CanConfigure ? "请先读取当前配置，建立可审核的端口与 VLAN 基线。" : "已建立配置基线；仅提交实际编辑的字段。";
    public bool SupportsConfiguration => SelectedDevice?.Role == DeviceRole.Switch && SelectedDevice.Vendor is Vendor.Huawei or Vendor.H3C or Vendor.Cisco or Vendor.Ruijie;
    public bool CanConfigure => SupportsConfiguration && SelectedDevice is not null && prepared.Contains(SelectedDevice.Id) && (SelectedDevice.IsSimulation || (SelectedConnection?.Protocol == ConnectionProtocol.Ssh && preparedConnection.GetValueOrDefault(SelectedDevice.Id) == SelectedConnection.Id));
    public bool IsInteractiveProfile => SelectedDevice?.IsSimulation == true || SelectedConnection?.Protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Console or ConnectionProtocol.Telnet;
    public string WorkflowLabel => IsApplying ? "正在执行与验证，请勿重复下发" : CanConfigure ? "已读取基线 → 选择端口 → 编辑字段 → 审核 → 执行与验证 → 保存" : "① 选择连接 → ② 读取当前配置 → ③ 图形化编辑 → ④ 审核与执行";
    public string SelectionLabel => $"已选 {Ports.Count(p => p.Picked)} 个端口";
    public string SelectionDetails => Ports.Count(p => p.Picked) == 1 ? Ports.Single(p => p.Picked).Detail : $"{SelectionLabel}；{CommonValue(p => p.ModeLabel, "模式") }；{CommonValue(p => p.Pvid.ToString(), "PVID")}";
    private string CommonValue(Func<PortState, string> selector, string label) { var values = Ports.Where(p => p.Picked).Select(p => selector(p.State)).Distinct().Take(2).ToArray(); return values.Length == 1 ? $"{label}：{values[0]}" : values.Length > 1 ? $"{label}：值不一致" : $"{label}：未选择"; }
    private bool editorOpen;
    public bool EditorOpen { get => editorOpen; set { Set(ref editorOpen, value); if (value) AiOpen = false; } }
    private bool aiOpen;
    public bool AiOpen { get => aiOpen; set { Set(ref aiOpen, value); if (value) EditorOpen = false; } }
    private string status = "从设备中心选择一台设备，再选择要完成的操作。";
    public string Status { get => status; set => Set(ref status, value); }
    private bool busy;
    public bool Busy { get => busy; private set { Set(ref busy, value); RefreshAll(); } }
    public bool Ready => !Busy;
    private bool isApplying;
    public bool IsApplying { get => isApplying; private set { Set(ref isApplying, value); RefreshAll(); } }
    private string operation = "";
    public string Operation { get => operation; private set { Set(ref operation, value); Changed(nameof(ConnectionStage)); Changed(nameof(NextStepHint)); } }
    private string connectionOutput = "尚未读取设备。";
    public string ConnectionOutput { get => connectionOutput; set => Set(ref connectionOutput, value); }

    private bool applyMode, applyPvid, applyAllowed, applyEnabled, applyDescription, createInlineVlan;
    public bool ApplyMode { get => applyMode; set { if (Set(ref applyMode, value)) DraftChanged(); } }
    public bool ApplyPvid { get => applyPvid; set { if (Set(ref applyPvid, value)) DraftChanged(); } }
    public bool ApplyAllowed { get => applyAllowed; set { if (Set(ref applyAllowed, value)) DraftChanged(); } }
    public bool ApplyEnabled { get => applyEnabled; set { if (Set(ref applyEnabled, value)) DraftChanged(); } }
    public bool ApplyDescription { get => applyDescription; set { if (Set(ref applyDescription, value)) DraftChanged(); } }
    public bool CreateInlineVlan { get => createInlineVlan; set { if (Set(ref createInlineVlan, value)) DraftChanged(); } }
    private Choice<PortMode>? selectedMode;
    public Choice<PortMode> SelectedMode { get => selectedMode ?? Modes[0]; set { if (Set(ref selectedMode, value)) { if (!loading) ApplyMode = true; DraftChanged(); Changed(nameof(ShowAllowed)); } } }
    public bool ShowAllowed => ApplyMode ? SelectedMode.Value == PortMode.Trunk : Ports.Any(p => p.Picked && p.State.Mode == PortMode.Trunk);
    private string pvid = "", allowedVlans = "", portDescription = "", newVlanName = "";
    public string Pvid { get => pvid; set { if (Set(ref pvid, value)) { if (!loading) ApplyPvid = true; DraftChanged(); } } }
    public string AllowedVlans { get => allowedVlans; set { if (Set(ref allowedVlans, value)) { if (!loading) ApplyAllowed = true; DraftChanged(); } } }
    public string PortDescription { get => portDescription; set { if (Set(ref portDescription, value)) { if (!loading) ApplyDescription = true; DraftChanged(); } } }
    public string NewVlanName { get => newVlanName; set { if (Set(ref newVlanName, value)) DraftChanged(); } }
    private bool portEnabled;
    public bool PortEnabled { get => portEnabled; set { if (Set(ref portEnabled, value)) { if (!loading) ApplyEnabled = true; DraftChanged(); } } }
    public string PortRange { get; set; } = "1-8";
    public int ModifiedFieldCount => new[] { ApplyMode, ApplyPvid, ApplyAllowed && ShowAllowed, ApplyEnabled, ApplyDescription }.Count(v => v);
    public string PortValidation
    {
        get
        {
            if (!CanConfigure) return CapabilityHint;
            if (!Ports.Any(p => p.Picked)) return "请选择一个或多个端口。";
            if (ModifiedFieldCount == 0) return "直接编辑需要修改的字段，其余字段保持原值。";
            try { var request = BuildPortRequest(); if (request.Pvid is < 1 or > 4094) return "PVID 必须为 1–4094。"; if (request.Pvid is int id && !AvailableVlans.Any(v => v.Id == id) && !CreateInlineVlan) return "此 VLAN 不存在，可勾选同步创建。"; ChangeEngine.ValidateRequest(RequireDevice(), ProjectedState() ?? workspace.GetState(RequireDevice().Id)!, request); }
            catch (ArgumentException ex) { return ex.Message; }
            return "";
        }
    }
    public bool CanPreviewPorts => CanConfigure && !IsApplying && PortValidation.Length == 0;
    public ChangeRequest BuildPortRequest()
    {
        if (!CanConfigure) throw new InvalidOperationException(CapabilityHint);
        var id = ApplyPvid ? ParseInt(Pvid, "PVID") : (int?)null;
        return new(CreateVlanId: CreateInlineVlan && ApplyPvid ? id : null, VlanName: CreateInlineVlan && ApplyPvid ? NewVlanName.Trim() : null,
            Ports: Ports.Where(p => p.Picked).Select(p => p.State.Name).ToArray(), Mode: ApplyMode ? SelectedMode.Value : null, Pvid: id,
            TaggedVlans: ApplyAllowed && ShowAllowed ? VlanRanges.Parse(AllowedVlans) : null, Enabled: ApplyEnabled ? PortEnabled : null, Description: ApplyDescription ? PortDescription.Trim() : null);
    }
    private void DraftChanged()
    {
        if (loading) return;
        InvalidatePlan(); SaveDraft(); Changed(nameof(ModifiedFieldCount)); Changed(nameof(PortValidation)); Changed(nameof(ShowAllowed)); RefreshCommands();
    }
    private void SelectionChanged()
    {
        if (loading) return;
        InvalidatePlan(); FillCurrentValues(); SaveDraft(); RefreshAll();
    }
    private void FillCurrentValues()
    {
        var items = Ports.Where(p => p.Picked).Select(p => p.State).ToArray(); loading = true;
        if (items.Length > 0)
        {
            if (!ApplyMode) SelectedMode = Modes.FirstOrDefault(m => m.Value == items[0].Mode) ?? Modes[0];
            if (!ApplyPvid) Pvid = items.Select(p => p.Pvid).Distinct().Count() == 1 ? items[0].Pvid.ToString() : "";
            if (!ApplyAllowed) AllowedVlans = items.Select(p => p.TaggedLabel).Distinct().Count() == 1 ? string.Join(",", items[0].TaggedVlans) : "";
            if (!ApplyEnabled) PortEnabled = items[0].Enabled;
            if (!ApplyDescription) PortDescription = items.Select(p => p.Description).Distinct().Count() == 1 ? items[0].Description : "";
        }
        loading = false;
    }
    private void SelectPorts(Func<PortItem, bool> predicate)
    { loading = true; foreach (var port in Ports) port.Picked = predicate(port); loading = false; SelectionChanged(); }
    private PortDraft CaptureDraft() => new(Ports.Where(p => p.Picked).Select(p => p.State.Name).ToArray(), ApplyMode, SelectedMode.Value, ApplyPvid, Pvid, ApplyAllowed, AllowedVlans, ApplyEnabled, PortEnabled, ApplyDescription, PortDescription, CreateInlineVlan, NewVlanName);
    private void SaveDraft() { if (!loading && SelectedDevice is not null) drafts[SelectedDevice.Id] = CaptureDraft(); }
    private void RestoreDraft()
    {
        loading = true; var draft = SelectedDevice is null ? null : drafts.GetValueOrDefault(SelectedDevice.Id);
        foreach (var port in Ports) port.Picked = draft?.Selected.Contains(port.State.Name) == true;
        ApplyMode = draft?.ModeOn ?? false; SelectedMode = Modes.First(m => m.Value == (draft?.Mode ?? PortMode.Access));
        ApplyPvid = draft?.PvidOn ?? false; Pvid = draft?.Pvid ?? ""; ApplyAllowed = draft?.VlansOn ?? false; AllowedVlans = draft?.Vlans ?? "";
        ApplyEnabled = draft?.EnabledOn ?? false; PortEnabled = draft?.Enabled ?? false; ApplyDescription = draft?.DescriptionOn ?? false; PortDescription = draft?.Description ?? "";
        CreateInlineVlan = draft?.CreateVlan ?? false; NewVlanName = draft?.NewVlanName ?? ""; loading = false; FillCurrentValues(); RefreshAll();
    }

    private string vlanId = "30", vlanName = "";
    public string VlanId { get => vlanId; set { Set(ref vlanId, value); InvalidatePlan(); Changed(nameof(VlanValidation)); RefreshCommands(); } }
    public string VlanName { get => vlanName; set { Set(ref vlanName, value); InvalidatePlan(); Changed(nameof(VlanValidation)); RefreshCommands(); } }
    private Choice<int>? vlanAction;
    public Choice<int> VlanAction { get => vlanAction ?? VlanActions[0]; set { Set(ref vlanAction, value); InvalidatePlan(); Changed(nameof(VlanValidation)); RefreshCommands(); } }
    public string VlanValidation
    {
        get { if (!CanConfigure) return CapabilityHint; try { var request = BuildVlanRequest(); ChangeEngine.ValidateRequest(RequireDevice(), ProjectedState() ?? workspace.GetState(RequireDevice().Id)!, request); if (request.RenameVlanId is int id && AvailableVlans.Single(v => v.Id == id).Name == request.VlanName) return "名称尚未修改。"; return ""; } catch (ArgumentException ex) { return ex.Message; } }
    }
    private VlanState? selectedVlan;
    public VlanState? SelectedVlan { get => selectedVlan; set { if (Set(ref selectedVlan, value) && value is not null) { VlanAction = VlanActions[1]; VlanId = value.Id.ToString(); VlanName = value.Name; } } }
    private ChangeRequest BuildVlanRequest() { var id = ParseInt(VlanId, "VLAN 编号"); return VlanAction.Value switch { 1 => new(RenameVlanId: id, VlanName: VlanName.Trim()), 2 => new(DeleteVlanId: id), _ => new(CreateVlanId: id, VlanName: VlanName.Trim()) }; }

    private ChangePlan? plan;
    public PlanDiff[] PlanDifferences => plan?.Differences ?? [];
    public string PlanCommands => string.Join(Environment.NewLine, plan?.Commands ?? []);
    public bool IsSecurityPlan => plan is not null && ChangeEngine.Requests(plan).Any(r => MacRules.SecurityChanges(r).Length > 0);
    public bool CanChooseSave => !IsSecurityPlan;
    public CommandExplanation[] PlanCommandRows => (plan?.Commands ?? []).Select(CommandExplanation.From).Concat(
        plan is not null && (IsSecurityPlan || SaveAfterVerify) ? [new CommandExplanation(NetworkOperations.SaveCommand(RequireDevice().Vendor), "回读验证通过后执行：写入设备启动配置；模拟设备不执行保存命令。")] : []).ToArray();
    private void NotifyPlan() { foreach (var name in new[] { nameof(PlanDifferences), nameof(PlanCommands), nameof(PlanCommandRows), nameof(PlanRisks), nameof(PlanTitle), nameof(IsLivePlan), nameof(IsSecurityPlan), nameof(CanChooseSave), nameof(SaveAfterVerify), nameof(ReviewScope) }) Changed(name); }
    public string PlanRisks => string.Join("\n", plan?.Risks.Select(r => "• " + r) ?? []);
    public string PlanTitle => plan is null ? "变更审核" : $"{SelectedDevice?.Name} · {plan.Differences.Length} 项变更";
    public bool IsLivePlan => plan?.IsSimulation == false;
    private bool reviewOpen;
    public bool ReviewOpen { get => reviewOpen; set { Set(ref reviewOpen, value); RefreshWorkbenchCommands(); } }
    private bool planConfirmed, liveRiskAccepted, saveAfterVerify = true;
    public bool PlanConfirmed { get => planConfirmed; set { Set(ref planConfirmed, value); RefreshCommands(); } }
    public bool LiveRiskAccepted { get => liveRiskAccepted; set { Set(ref liveRiskAccepted, value); RefreshCommands(); } }
    public bool SaveAfterVerify { get => IsSecurityPlan || saveAfterVerify; set { if (Set(ref saveAfterVerify, value)) { Changed(nameof(SaveAfterVerify)); Changed(nameof(PlanCommandRows)); } } }
    private void ShowPlan(ChangeRequest request)
    {
        plan = engine.Preview(RequireDevice(), request); reviewIncludesCurrent = true; PlanConfirmed = false; LiveRiskAccepted = false;
        NotifyPlan();
        ReviewOpen = true; Status = "变更尚未下发，请审核差异和风险。"; RefreshCommands();
    }
    private void InvalidatePlan()
    { engine?.Invalidate(); plan = null; NotifyPlan(); ReviewOpen = false; PlanConfirmed = false; LiveRiskAccepted = false; }
    private async Task ApplyAsync()
    {
        if (plan is null) throw new InvalidOperationException("计划已失效，请重新预览。");
        var selectedPlan = plan; var device = RequireDevice(); var profile = RequireProfile(); IsApplying = true;
        try
        {
            if (selectedPlan.IsSimulation) { engine.ApplySimulation(selectedPlan.Id, PlanConfirmed); Status = "模拟下发、自动备份和回读验证完成。启动配置保存仅在真实设备执行。"; }
            else
            {
                if (!LiveRiskAccepted) throw new InvalidOperationException("请确认真实设备风险与带外恢复准备。");
                var validPlan = engine.ConsumePlan(selectedPlan.Id, PlanConfirmed);
                Status = await network.ApplyAsync(device, profile, validPlan, SaveAfterVerify, new Progress<string>(s => Operation = s), operationCancellation!.Token);
            }
            loading = true; ApplyMode = ApplyPvid = ApplyAllowed = ApplyEnabled = ApplyDescription = CreateInlineVlan = false; loading = false;
            if (selectedPlan.Request.AddMacBindings?.Length > 0) MacBatch = "";
            foreach (var item in PendingChanges.Where(p => p.DeviceId == device.Id).ToArray()) PendingChanges.Remove(item);
            queueBaselines.Remove(device.Id); MacRows.Clear(); NotifyQueue();
            EditorOpen = false; LastResult = Status;
            SaveDraft(); RefreshCurrent(); InvalidatePlan();
        }
        catch { if (!device.IsSimulation) { prepared.Remove(device.Id); InvalidatePlan(); } throw; }
        finally { IsApplying = false; }
    }

    private Snapshot? selectedSnapshot;
    public Snapshot? SelectedSnapshot { get => selectedSnapshot; set { Set(ref selectedSnapshot, value); SnapshotText = value?.Content ?? "选择一份备份查看内容。"; RefreshCommands(); } }
    private string snapshotText = "选择一份备份查看内容。";
    public string SnapshotText { get => snapshotText; set => Set(ref snapshotText, value); }
    private void RefreshHistory()
    {
        var id = SelectedSnapshot?.Id; Snapshots.Clear(); Audit.Clear(); if (SelectedDevice is null) return;
        foreach (var s in workspace.GetSnapshots(SelectedDevice.Id)) Snapshots.Add(s);
        foreach (var a in workspace.GetAudit(SelectedDevice.Id)) Audit.Add(a);
        SelectedSnapshot = Snapshots.FirstOrDefault(s => s.Id == id);
    }
    private void RefreshCurrent(bool preserve = true)
    {
        if (preserve) SaveDraft(); loading = true; Ports.Clear(); Vlans.Clear(); MacBindings.Clear();
        if (SelectedDevice is not null)
        {
            var state = workspace.GetState(SelectedDevice.Id);
            var multiple = state?.Ports.Select(p => p.Name[..Math.Max(0, p.Name.LastIndexOf('/'))]).Distinct().Count() > 1;
            foreach (var p in state?.Ports ?? []) { var item = new PortItem(p, multiple); item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PortItem.Picked)) SelectionChanged(); }; Ports.Add(item); }
            foreach (var v in state?.Vlans ?? []) Vlans.Add(v);
            foreach (var b in state?.MacBindings ?? []) { var item = new MacItem(b, state?.PortSecurity?.SingleOrDefault(s => s.PortName == b.PortName)); item.PropertyChanged += (_, _) => { InvalidatePlan(); RefreshCommands(); RefreshWorkbenchCommands(); }; MacBindings.Add(item); }
        }
        loading = false; RestoreDraft(); RefreshHistory(); Changed(nameof(MembershipRows)); RefreshAll();
    }
    public void RefreshWorkspace() { var id = SelectedDevice?.Id; Devices.Clear(); foreach (var d in workspace.GetDevices()) Devices.Add(d); selectedDevice = null; SelectedDevice = Devices.FirstOrDefault(d => d.Id == id && !d.Archived) ?? Devices.FirstOrDefault(d => !d.Archived); Changed(nameof(FilteredDevices)); RefreshObservability(); }
    public void SaveDevice(Device device, string username, string password, string privateKey, ConnectionProfile profile, bool replaceSecret)
    {
        profile.Validate(); device = device with { Connections = [profile] }; device.Validate(); var old = workspace.GetDevices().SingleOrDefault(d => d.Id == device.Id);
        if (old is not null && old.IsSimulation != device.IsSimulation) throw new ArgumentException("已有设备不能切换模拟/真实模式，请另建档案。");
        string? created = null;
        if (replaceSecret)
        {
            var previous = profile.CredentialReference is null ? null : JsonSerializer.Deserialize<DeviceCredential>(vault.Read(profile.CredentialReference));
            var key = string.IsNullOrWhiteSpace(privateKey) ? previous?.PrivateKey : privateKey;
            profile = profile with { CredentialReference = created = vault.Store(JsonSerializer.Serialize(new DeviceCredential(username, password.Length == 0 ? previous?.Password ?? "" : password, key, password.Length == 0 ? previous?.KeyPassphrase : password))) };
        }
        var profiles = old?.GetConnections().Where(p => p.Id != profile.Id).Append(profile).ToArray() ?? [profile];
        device = device with { Connections = profiles, PreferredConnectionId = profile.Id };
        try { if (old is null) DemoWorkspace.Add(workspace, device); else workspace.SaveDevice(device); }
        catch { if (created is not null) vault.Remove(created); throw; }
        prepared.Remove(device.Id); drafts.Remove(device.Id); RefreshWorkspace(); SelectedDevice = Devices.Single(d => d.Id == device.Id); Status = "设备与连接配置已保存。请读取当前配置。";
    }
    public string CredentialUsername(string? reference) => reference is null ? "" : JsonSerializer.Deserialize<DeviceCredential>(vault.Read(reference))?.Username ?? "";
    public int ImportDevices(IReadOnlyList<Device> devices)
    {
        workspace.ImportDevices(devices);
        RefreshWorkspace(); Status = $"已导入 {devices.Count} 台设备，未自动连接。"; return devices.Count;
    }
    public void ArchiveSelected() { var d = RequireDevice(); if (TerminalConnected || Busy) throw new InvalidOperationException("请先结束当前设备操作。"); workspace.SaveDevice(d with { Archived = true }); prepared.Remove(d.Id); drafts.Remove(d.Id); RefreshWorkspace(); ReturnToDevices(); Status = "设备已归档，备份与审计仍保留。"; }
    private async Task ReadCurrentAsync()
    {
        var device = RequireDevice(); var profile = RequireProfile();
        prepared.Remove(device.Id); InvalidatePlan(); RefreshAll();
        if (device.IsSimulation) { prepared.Add(device.Id); ConnectionOutput = "模拟连接已建立，已读取本地端口与 VLAN 基线；没有访问真实网络。"; }
        else if (profile.Protocol == ConnectionProtocol.Ssh)
        {
            var result = await network.ReadConfigurationAsync(device, profile, operationCancellation!.Token);
            if (result.State is not null) workspace.SaveState(result.State);
            readResults[device.Id] = result.Raw;
            var updated = workspace.GetDevices().Single(d => d.Id == device.Id);
            if (SelectedDevice?.Id == device.Id && updated != SelectedDevice) { RefreshWorkspace(); selectedConnection = Connections.FirstOrDefault(p => p.Id == profile.Id); Changed(nameof(SelectedConnection)); }
            if (result.State is not null) { prepared.Add(device.Id); preparedConnection[device.Id] = profile.Id; }
            if (SelectedDevice?.Id == device.Id) ConnectionOutput = result.Raw;
            if (result.Warning is not null) { Status = result.Warning; if (SelectedDevice?.Id == device.Id) RefreshCurrent(); return; }
        }
        else
        {
            var result = await network.ReadProtocolAsync(device, profile, operationCancellation!.Token);
            readResults[device.Id] = result; if (SelectedDevice?.Id == device.Id) ConnectionOutput = result;
        }
        if (SelectedDevice?.Id == device.Id) RefreshCurrent(); Status = "读取完成。可查看配置结果及当前设备能力。";
    }
    private async Task BackupAsync()
    {
        var device = RequireDevice(); var profile = RequireProfile();
        var content = device.IsSimulation ? JsonSerializer.Serialize(workspace.GetState(device.Id), ChangeEngine.JsonOptions)
            : profile.Protocol == ConnectionProtocol.Ssh ? (await network.ReadConfigurationAsync(device, profile, operationCancellation!.Token)).Raw
            : await network.ReadProtocolAsync(device, profile, operationCancellation!.Token);
        workspace.AddSnapshot(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, device.IsSimulation ? "手动模拟备份" : $"{profile.ProtocolLabel} 只读备份", content));
        workspace.AddAudit(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "配置备份", "已加密保存"));
        if (SelectedDevice?.Id == device.Id) RefreshHistory(); Status = "备份已保存，端口选择和编辑草稿保持不变。";
    }
    private void Compare()
    {
        var device = RequireDevice(); var selected = SelectedSnapshot ?? throw new InvalidOperationException("请选择备份。");
        var current = JsonSerializer.Serialize(workspace.GetState(device.Id), ChangeEngine.JsonOptions).Split('\n'); var previous = selected.Content.Split('\n');
        SnapshotText = "逐行位置对比（− 备份 / + 当前），不代表厂商语义差异。\n\n" + string.Join("\n", Enumerable.Range(0, Math.Max(current.Length, previous.Length)).SelectMany(i => i < current.Length && i < previous.Length && current[i] == previous[i] ? Array.Empty<string>() : new[] { i < previous.Length ? "− " + previous[i] : "", i < current.Length ? "+ " + current[i] : "" }));
    }

    private bool terminalConnected;
    public bool TerminalConnected { get => terminalConnected; private set { Set(ref terminalConnected, value); RefreshAll(); } }
    private string terminalOutput = "高级终端支持 SSH、Console 和 Telnet；按需连接。模拟设备仅提供离线命令演示。";
    public string TerminalOutput { get => terminalOutput; private set => Set(ref terminalOutput, value); }
    private string terminalInput = "";
    public string TerminalInput { get => terminalInput; set { Set(ref terminalInput, value); RefreshCommands(); } }
    public bool SecretInput { get; set; }
    private async Task ConnectTerminalAsync()
    {
        var device = RequireDevice(); var profile = RequireProfile(); Section = 4;
        if (device.IsSimulation) { TerminalConnected = true; TerminalOutput = "模拟终端已打开；命令仅在本地演示，不连接设备。\n"; return; }
        terminal = sessions.Create(profile.Protocol);
        try { await terminal.ConnectAsync(device, profile, operationCancellation!.Token); }
        catch { await terminal.DisposeAsync(); terminal = null; throw; }
        TerminalConnected = true; TerminalOutput = $"已打开 {profile.ProtocolLabel} 会话。Console / Telnet 登录交互由用户操作。\n";
        terminalCancellation = new(); terminalPump = PumpTerminalAsync(terminalCancellation.Token);
    }
    private async Task PumpTerminalAsync(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested && terminal is not null) { var text = await terminal.ReadAvailableAsync(ct); if (text.Length > 0) AppendTerminal(CliConversation.Clean(text)); if (!terminal.IsConnected) throw new IOException("会话已断开。"); await Task.Delay(150, ct); } }
        catch (OperationCanceledException) { }
        catch { Status = "终端连接已中断，请检查连接后重新连接。"; await CloseSessionAsync(); }
    }
    private async Task SendTerminalAsync()
    {
        var input = TerminalInput; if (input.Length > 4000) { Status = "单次终端输入最多 4000 字符。"; return; }
        try
        {
            if (!SecretInput && !CommandHistory.Contains(input)) { CommandHistory.Insert(0, input); if (CommandHistory.Count > 100) CommandHistory.RemoveAt(100); }
            if (SelectedDevice?.IsSimulation == true) AppendTerminal($"\n> {(SecretInput ? "[隐藏输入]" : input)}\n模拟设备：仅演示命令输入，配置请使用图形化审核流程。\n");
            else if (terminal is not null) await terminal.SendAsync(input + "\r\n", CancellationToken.None);
            TerminalInput = "";
        }
        catch { Status = "终端发送未完成，请检查连接；不会自动重发。"; }
    }
    private void AppendTerminal(string text) { var output = TerminalOutput + text; TerminalOutput = output.Length > 200000 ? output[^200000..] : output; }
    public async Task DisconnectAsync() { terminalCancellation?.Cancel(); if (terminalPump is not null) await terminalPump; terminalPump = null; await CloseSessionAsync(); Status = "终端已断开，记录仅保留在本次运行内存中。"; }
    private async Task CloseSessionAsync() { if (terminal is not null) await terminal.DisposeAsync(); terminal = null; TerminalConnected = false; terminalCancellation?.Dispose(); terminalCancellation = null; }

    private AiProfile? selectedProfile;
    public AiProfile? SelectedProfile { get => selectedProfile; set { if (Set(ref selectedProfile, value)) { ContextApproved = false; RefreshCommands(); } } }
    private AiProfile? editingProfile;
    public AiProfile? EditingProfile { get => editingProfile; set { if (Set(ref editingProfile, value) && value is not null) { ProfileName = value.Name; ProfileUrl = value.BaseUrl; ProfileModel = value.Model; SelectedProtocol = Protocols.Single(p => p.Value == value.Protocol); Changed(nameof(ProfileName)); Changed(nameof(ProfileUrl)); Changed(nameof(ProfileModel)); } } }
    public string ProfileName { get; set; } = "我的 AI 服务";
    public string ProfileUrl { get; set; } = "https://api.deepseek.com";
    public string ProfileModel { get; set; } = "";
    private Choice<AiProtocol>? selectedProtocol;
    public Choice<AiProtocol> SelectedProtocol { get => selectedProtocol ?? Protocols[3]; set => Set(ref selectedProtocol, value); }
    private string aiQuestion = "";
    public string AiQuestion { get => aiQuestion; set { Set(ref aiQuestion, value); RefreshCommands(); } }
    private bool contextApproved;
    public bool ContextApproved { get => contextApproved; set { Set(ref contextApproved, value); RefreshCommands(); } }
    public string ContextPreview => SelectedDevice is null ? "请选择设备。" : AiContext.Build(SelectedDevice, workspace.GetState(SelectedDevice.Id));
    private string aiAnswer = "按需打开助手，配置 AI 服务并核对发送内容。";
    public string AiAnswer { get => aiAnswer; private set => Set(ref aiAnswer, value); }
    public void SaveProfile(string apiKey, bool asNew)
    {
        var old = asNew ? null : EditingProfile;
        var profile = new AiProfile(old?.Id ?? Guid.NewGuid(), ProfileName.Trim(), SelectedProtocol.Value, ProfileUrl.Trim(), ProfileModel.Trim(), old?.CredentialReference, IsDefault: old?.IsDefault ?? Profiles.Count == 0);
        AiClient.Validate(profile);
        if (string.IsNullOrWhiteSpace(apiKey) && profile.CredentialReference is null && profile.Protocol != AiProtocol.Ollama) throw new ArgumentException("请输入 API 密钥。");
        string? created = null;
        if (!string.IsNullOrWhiteSpace(apiKey)) profile = profile with { CredentialReference = created = vault.Store(apiKey.Trim()) };
        try { workspace.SaveAiProfile(profile); } catch { if (created is not null) vault.Remove(created); throw; }
        if (old is not null) Profiles.Remove(old); Profiles.Add(profile); SelectedProfile = profile; EditingProfile = profile; ContextApproved = false; Status = "AI 配置已加密保存。";
    }
    public void SetDefaultProfile()
    {
        if (EditingProfile is null) throw new ArgumentException("请先保存并选择 AI 配置。");
        var chosen = EditingProfile.Id; var active = SelectedProfile?.Id;
        var all = Profiles.Select(p => p with { IsDefault = p.Id == chosen }).ToArray();
        foreach (var profile in all) workspace.SaveAiProfile(profile);
        Profiles.Clear(); foreach (var profile in all) Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == active); EditingProfile = Profiles.Single(p => p.Id == chosen); Status = "默认 AI 配置已保存，下次启动自动选择。";
    }
    private async Task AskAiAsync()
    {
        var device = RequireDevice(); var profile = SelectedProfile ?? throw new InvalidOperationException("请先配置 AI 服务。");
        if (!ContextApproved) throw new InvalidOperationException("请先核对目标地址与上下文。");
        var context = ContextPreview; var question = AiQuestion; ContextApproved = false; AiAnswer = "正在请求分析；可以继续浏览其他页面。";
        try { var answer = await ai.AnalyzeAsync(profile, question, context, operationCancellation!.Token); AiAnswer = $"{device.Name} · {profile.Name}\n\n{answer}"; workspace.AddAudit(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "AI 辅助分析", "用户确认发送白名单上下文；未执行命令")); if (SelectedDevice?.Id == device.Id) RefreshHistory(); Status = "AI 分析完成，端口选择与草稿保持不变。"; }
        catch { AiAnswer = "分析未完成，请查看当前操作提示。"; throw; }
    }
    public void UseProtocolPreset()
    { ProfileUrl = SelectedProtocol.Value switch { AiProtocol.Responses => "https://api.openai.com/v1", AiProtocol.ClaudeMessages => "https://api.anthropic.com/v1", AiProtocol.Gemini => "https://generativelanguage.googleapis.com/v1beta", AiProtocol.Ollama => "http://localhost:11434", _ => "https://api.deepseek.com" }; Changed(nameof(ProfileUrl)); }

    private Device RequireDevice() => SelectedDevice ?? throw new InvalidOperationException("请选择设备。");
    private ConnectionProfile RequireProfile() => SelectedConnection ?? throw new InvalidOperationException("请选择连接配置。");
    private static int ParseInt(string value, string name) => int.TryParse(value, out var n) ? n : throw new ArgumentException($"{name}必须为整数。");
    private void Guard(Action action) { try { action(); } catch (Exception ex) { Status = Error(ex); } }
    private async Task RunAsync(string name, Func<Task> action)
    {
        if (Busy) return; Busy = true; Operation = name; operationCancellation = new();
        try { await action(); } catch (OperationCanceledException) { Status = "操作取消或超时；涉及真实下发时请检查设备是否部分生效。"; }
        catch (Exception ex) { Status = Error(ex); }
        finally { operationCancellation.Dispose(); operationCancellation = null; Busy = false; Operation = ""; RefreshAll(); }
    }
    private static string Error(Exception ex) => ex is ArgumentException or InvalidOperationException or NotSupportedException ? ex.Message : "操作未完成，请检查连接、认证、权限或设备兼容性。敏感原文未写入日志。";
    private void RefreshCommands() { foreach (var command in commands ?? []) command.Refresh(); RefreshWorkbenchCommands(); RefreshGuidedCommands(); }
    private void RefreshAll()
    { foreach (var name in new[] { nameof(DeviceSummary), nameof(InventorySummary), nameof(StackMembers), nameof(PortGroups), nameof(CapabilityHint), nameof(SupportsConfiguration), nameof(CanConfigure), nameof(IsInteractiveProfile), nameof(WorkflowLabel), nameof(SelectionLabel), nameof(SelectionDetails), nameof(ModifiedFieldCount), nameof(PortValidation), nameof(VlanValidation), nameof(MacValidation), nameof(ShowAllowed), nameof(CanPreviewPorts), nameof(ContextPreview), nameof(Ready), nameof(ConnectionStage), nameof(ReadCurrentLabel), nameof(NextStepHint), nameof(PortNames), nameof(SelectedDeviceMetrics), nameof(SelectedHistory), nameof(AvailableVlans) }) Changed(name); RefreshCommands(); }
    public void Dispose() { operationCancellation?.Cancel(); terminalCancellation?.Cancel(); if (terminal is not null) _ = terminal.DisposeAsync(); }
}
