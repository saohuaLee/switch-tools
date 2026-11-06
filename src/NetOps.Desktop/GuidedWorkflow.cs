using NetOps.Application;
using NetOps.Domain;

namespace NetOps.Desktop;

public sealed partial class MainViewModel
{
    private bool taskLanding = true;
    private string lastResult = "";
    private bool reviewIncludesCurrent;
    private readonly HashSet<Guid> reviewedDraftIds = [];
    private bool includeSavedDrafts;
    public bool IncludeSavedDrafts { get => includeSavedDrafts; set { if (Set(ref includeSavedDrafts, value)) InvalidatePlan(); } }
    public bool TaskLanding { get => taskLanding; set { if (Set(ref taskLanding, value)) NotifyTask(); } }
    public bool TaskFormVisible => !TaskLanding;
    public bool NeedsTaskRead => !TaskLanding && Section is 1 or 2 or 6 && !CanConfigure;
    public bool TaskContentVisible => !TaskLanding && !NeedsTaskRead;
    public bool ConnectionControlsVisible => !EasyActive && (!CanConfigure || Section == 4);
    public string TaskTitle => Section switch { 1 => "修改端口配置", 2 => "管理 VLAN", 3 => "备份与操作记录", 4 => "手动命令终端", 6 => "绑定 / 更换 MAC 地址", _ => "查看运行状态" };
    public string TaskInstructions => Section switch
    {
        1 => "先点选要修改的端口，再点“编辑所选端口”；填完后直接审核并执行。",
        2 => "选择创建、重命名或删除 VLAN，填写编号与名称，然后直接审核并执行。",
        3 => "点击“备份当前配置”保存快照，选择下方记录查看内容与操作结果。",
        4 => "先连接终端，再输入厂商命令；手动命令会直接执行，请核对目标设备。",
        6 => "先选目标端口，再输入一条或多条 MAC；更换时从当前绑定中选择旧地址。",
        _ => "查看设备当前的监控指标、堆叠成员及最近读取的配置。"
    };
    public string WorkflowSteps => TaskLanding ? "① 选择设备  →  ② 选择要做的事  →  ③ 填写配置  →  ④ 审核执行" : $"当前设备：{SelectedDevice?.Name}  /  {TaskTitle}";
    public string LastResult { get => lastResult; private set { Set(ref lastResult, value); Changed(nameof(HasLastResult)); } }
    public bool HasLastResult => LastResult.Length > 0;
    public string ReviewScope => !reviewIncludesCurrent ? $"本次审核本机已存的 {reviewedDraftIds.Count} 组批量草稿，下面逐项列出；尚未下发。" : reviewedDraftIds.Count > 0 ? $"本次审核包含当前填写内容及明确选入的 {reviewedDraftIds.Count} 组批量草稿。" : "本次只修改当前操作，不包含其他批量草稿。确认前不会修改设备。";
    public ActionCommand ChooseTask { get; private set; } = null!;
    public ActionCommand BackToTasks { get; private set; } = null!;
    public ActionCommand ReviewPortOperation { get; private set; } = null!;
    public ActionCommand ReviewVlanOperation { get; private set; } = null!;
    public ActionCommand ReviewMacOperation { get; private set; } = null!;
    public ActionCommand ReviewDeleteMacOperation { get; private set; } = null!;
    public ActionCommand BindSelectedPorts { get; private set; } = null!;
    private void InitializeGuidedWorkflow()
    {
        ChooseTask = new(p => { ResetEasy(); Section = int.Parse(p?.ToString() ?? "0"); TaskLanding = false; LastResult = ""; NotifyTask(); }, () => !IsApplying && !ReviewOpen);
        BackToTasks = new(_ => { EditorOpen = false; TaskLanding = true; }, () => !IsApplying && !ReviewOpen);
        ReviewPortOperation = new(_ => Guard(() => ReviewOperation(BuildPortRequest())), () => CanPreviewPorts && Ready);
        ReviewVlanOperation = new(_ => Guard(() => ReviewOperation(BuildVlanRequest())), () => CanConfigure && Ready && VlanValidation.Length == 0);
        ReviewMacOperation = new(_ => RunMacEditorAction(() => { if (!string.IsNullOrWhiteSpace(MacPasteText)) ImportMacDrafts(); NormalizeMacDrafts(); ReviewOperation(BuildMacRequest()); }), () => CanConfigure && Ready && (MacRows.Count > 0 || !string.IsNullOrWhiteSpace(MacPasteText) || !string.IsNullOrWhiteSpace(MacBatch)));
        ReviewDeleteMacOperation = new(_ => Guard(() => ReviewOperation(BuildDeleteMacRequest())), () => CanConfigure && Ready && MacBindings.Any(m => m.Picked));
        BindSelectedPorts = new(_ => { ChooseTask.Execute("6"); if (MacRows.Count == 0) AddMacRow.Execute(null); }, () => CanConfigure && Ready && Ports.Any(p => p.Picked));
        PropertyChanged += (_, e) => { if (e.PropertyName is nameof(CanConfigure) or nameof(Section) or nameof(SelectedDevice) or nameof(DevicePendingCount)) NotifyTask(); };
    }
    private void NotifyTask()
    {
        if (Section == 6 && CanConfigure && !PortNames.Contains(MacPastePort)) MacPastePort = Ports.FirstOrDefault(p => p.Picked)?.State.Name ?? Ports.FirstOrDefault(p => p.State.Mode == PortMode.Access)?.State.Name ?? "";
        foreach (var name in new[] { nameof(TaskFormVisible), nameof(TaskTitle), nameof(TaskInstructions), nameof(NeedsTaskRead), nameof(TaskContentVisible), nameof(ConnectionControlsVisible), nameof(LegacyTaskReadVisible), nameof(LegacyTaskContentVisible), nameof(LegacyTaskHeadingVisible), nameof(WorkflowSteps), nameof(ReviewScope), nameof(MacVisibleBindings), nameof(HasMacVisibleBindings) }) Changed(name);
        RefreshGuidedCommands();
    }
    private void RefreshGuidedCommands() { foreach (var command in new[] { ChooseTask, BackToTasks, ReviewPortOperation, ReviewVlanOperation, ReviewMacOperation, ReviewDeleteMacOperation, BindSelectedPorts }) command?.Refresh(); }
    private void ReviewOperation(ChangeRequest request)
    {
        var device = RequireDevice(); var state = workspace.GetState(device.Id)!;
        var included = IncludeSavedDrafts ? PendingChanges.Where(p => p.DeviceId == device.Id).ToArray() : [];
        var drafts = included.Select(p => p.Request).ToArray();
        if (drafts.Length > 0 && (!queueBaselines.TryGetValue(device.Id, out var baseline) || baseline.Hash != ChangeEngine.Hash(state) || baseline.Identity != ChangeEngine.IdentityHash(device)))
            throw new InvalidOperationException("批量草稿的设备或基线已经变化，请先核对并移除失效草稿。");
        plan = engine.PreviewMany(device, drafts.Append(request).ToArray()); reviewIncludesCurrent = true; PlanConfirmed = false; LiveRiskAccepted = false;
        reviewedDraftIds.Clear(); reviewedDraftIds.UnionWith(included.Select(p => p.Id));
        NotifyPlan(); Changed(nameof(ReviewScope)); ReviewOpen = true; LastResult = ""; Status = "请审核本次修改，确认后才会备份、执行并验证。"; RefreshCommands();
    }
    public IEnumerable<MacItem> MacVisibleBindings => MacBindings.Where(m => (MacPastePort.Length == 0 || m.Binding.PortName == MacPastePort) && m.Binding.Kind == SelectedMacKind.Value);
    public bool HasMacVisibleBindings => MacVisibleBindings.Any();
}
