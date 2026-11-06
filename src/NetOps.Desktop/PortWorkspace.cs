using System.Collections.ObjectModel;
using NetOps.Application;
using NetOps.Domain;

namespace NetOps.Desktop;

public sealed class KeptMac(MacBinding binding) : Observable
{
    public MacBinding Binding { get; } = binding;
    public string Address => MacRules.VendorAddress(Binding.Address, false);
    private bool keep = true;
    private bool reentered;
    public bool Keep { get => keep; set => Set(ref keep, value); }
    public bool Reentered { get => reentered; set { if (Set(ref reentered, value)) Changed(nameof(Disposition)); } }
    public string Disposition => Keep ? "继续保留" : Reentered ? "新增中再次填写，仍保留" : "应用时移除";
    public void NotifyDisposition() => Changed(nameof(Disposition));
}

public sealed partial class MainViewModel
{
    private sealed record PortForm(string Baseline, string Name, int? Network, bool Enabled, bool NewNetwork, string Number, string Addresses, string[] Keep, bool Automatic, string Maximum, string Violation, bool RulesTouched, int Tab);
    private readonly Dictionary<(Guid, string), PortForm> portForms = [];
    private PortItem? workPort;
    private DeviceState? workBaseline;
    private Guid workDevice;
    private bool workLoading, workReset, workEnabled, workNewNetwork, workAutomatic = true, workRulesTouched, workReview, workReviewIsPort;
    private int workTab;
    private string workSearch = "", workName = "", workNumber = "", workAddresses = "", workMaximum = "1", workError = "", workNotice = "";
    private bool workNoticeFailed;
    private string? workRefreshPort;
    private Choice<int>? workNetwork;
    private Choice<string>? workViolation;
    public ObservableCollection<KeptMac> WorkExistingMacs { get; } = [];
    public PortItem? WorkPort
    {
        get => workPort;
        set
        {
            if (workLoading || Busy || ReviewOpen || ReferenceEquals(value, workPort)) return;
            SavePortForm(); workNotice = ""; workPort = value; Changed(); LoadPortForm();
        }
    }
    public string WorkSearch { get => workSearch; set { if (Set(ref workSearch, value)) Changed(nameof(WorkPorts)); } }
    public IEnumerable<PortItem> WorkPorts => Ports.Where(p => $"{p.State.Name} {p.State.Description} {p.State.Pvid}".Contains(WorkSearch.Trim(), StringComparison.OrdinalIgnoreCase));
    public bool WorkHasPort => WorkPort is not null;
    public bool WorkCanSelect => Ready && !ReviewOpen;
    public bool WorkCanEdit => WorkCanSelect && CanConfigure && WorkHasPort;
    public bool WorkNeedsRead => !CanConfigure;
    public string WorkPortTitle => WorkPort is null ? "从左边选择端口" : $"{WorkPort.FriendlyName} · {WorkPort.State.Name}";
    public string WorkPortCaption => WorkPort is null ? "点一下插口所在的行，右边就能修改。" : $"当前网络 {WorkPort.State.Pvid} · {(WorkPort.State.Enabled ? "端口已打开" : "端口已关闭")} · {WorkPort.State.ModeLabel}模式";
    public string WorkListCaption => $"端口列表 · {Ports.Count} 个";
    public int WorkTab { get => workTab; set { if (Set(ref workTab, value)) WorkChanged(); } }
    public bool WorkEditingMac => WorkTab == 1;
    public string WorkName { get => workName; set { if (Set(ref workName, value)) WorkChanged(); } }
    public Choice<int>? WorkNetwork { get => workNetwork; set { if (Set(ref workNetwork, value)) WorkChanged(); } }
    public bool WorkEnabled { get => workEnabled; set { if (Set(ref workEnabled, value)) WorkChanged(); } }
    public bool WorkNewNetwork { get => workNewNetwork; set { if (Set(ref workNewNetwork, value)) WorkChanged(); } }
    public string WorkNetworkNumber { get => workNumber; set { if (Set(ref workNumber, value)) WorkChanged(); } }
    public string WorkAddresses { get => workAddresses; set { if (Set(ref workAddresses, value)) WorkChanged(); } }
    public bool WorkAutomaticMaximum { get => workAutomatic; set { if (value == workAutomatic) return; if (!value) { try { workMaximum = Math.Max(1, WorkMacList.Target.Length).ToString(); Changed(nameof(WorkMaximum)); } catch (ArgumentException) { } } workAutomatic = value; Changed(); workRulesTouched = true; WorkChanged(); } }
    public bool WorkManualMaximum => !WorkAutomaticMaximum;
    public string WorkMaximum { get => workMaximum; set { if (Set(ref workMaximum, value)) { workRulesTouched = true; WorkChanged(); } } }
    public Choice<string> WorkViolation { get => workViolation ?? MacViolations[0]; set { if (Set(ref workViolation, value)) { workRulesTouched = true; WorkChanged(); } } }
    public string WorkError => workError;
    public bool WorkHasError => workError.Length > 0;
    public string WorkNotice => workNotice;
    public bool WorkHasNotice => workNotice.Length > 0;
    public string WorkNoticeColor => workNoticeFailed ? "#B42332" : workNotice.Contains("未确认") || workNotice.Contains("尚未保存") ? "#9A650D" : "#146C50";
    public bool WorkNoExistingMacs => WorkExistingMacs.Count == 0;
    public string WorkOriginalMacHeading => $"端口原有的 {WorkExistingMacs.Count} 个地址";
    public bool WorkMacSupported => SelectedDevice?.Vendor == Vendor.Cisco && WorkPort?.State is { Mode: PortMode.Access, IsUplink: false };
    private PortMacList WorkMacList => workBaseline is not null && WorkPort is not null ? PortMacList.Prepare(workBaseline, WorkPort.State.Name, WorkExistingMacs.Where(m => m.Keep).Select(m => m.Binding.Address), WorkAddresses) : throw new ArgumentException("先选择一个端口。");
    public string WorkMacSummary
    {
        get
        {
            if (!WorkHasPort) return "";
            try
            {
                var list = WorkMacList;
                return $"原有 {list.Original.Length} 个：保留 {list.Kept.Length} 个、移除 {list.Removed.Length} 个；本次输入 {list.InputCount} 条，实际新增 {list.Added.Length} 个。应用后共 {list.Target.Length} 个。";
            }
            catch (ArgumentException ex) { return ex.Message; }
        }
    }
    public string WorkLimitSummary
    {
        get { try { var list = WorkMacList; var count = list.Target.Length; return count == 0 ? list.Original.Length == 0 ? "还没有绑定地址；填写后会自动计算允许数量。" : "当前填写会清空名单，应用后解除该端口的 MAC 接入限制。" : WorkAutomaticMaximum ? $"自动允许 {count} 个设备，与应用后的名单一致。" : $"自定义允许数量：{WorkMaximum}。应用后的名单有 {count} 个地址。"; } catch (ArgumentException) { return "请先检查新增地址的格式。"; } }
    }
    public string WorkCurrentRule
    {
        get { var p = workBaseline is null || WorkPort is null ? null : MacRules.Security(workBaseline).FirstOrDefault(s => s.PortName == WorkPort.State.Name); return p is null ? "当前未启用端口安全限制。" : $"当前规则：允许上限 {p.Maximum}，{ChangeEngine.ViolationDescription(p.Violation)}。"; }
    }
    public bool WorkReviewVisible => workReview && ReviewOpen;
    public string WorkReviewSummary { get; private set; } = "";
    public EasyReviewRow[] WorkReviewRows { get; private set; } = [];
    public ActionCommand ReviewWorkPort { get; private set; } = null!;
    public ActionCommand ResetWorkPort { get; private set; } = null!;
    public ActionCommand KeepAllWorkMacs { get; private set; } = null!;
    public ActionCommand ReplaceAllWorkMacs { get; private set; } = null!;
    public ActionCommand AutomaticWorkMaximum { get; private set; } = null!;
    public ActionCommand FormatWorkMacs { get; private set; } = null!;
    private void InitializePortWorkspace()
    {
        ReviewWorkPort = new(_ => PreviewPortForm(), () => WorkCanEdit);
        ResetWorkPort = new(_ => { if (WorkPort is null) return; portForms.Remove((workDevice, WorkPort.State.Name)); LoadPortForm(); }, () => WorkCanEdit);
        KeepAllWorkMacs = new(_ => { foreach (var m in WorkExistingMacs) m.Keep = true; }, () => WorkCanEdit);
        ReplaceAllWorkMacs = new(_ => { foreach (var m in WorkExistingMacs) m.Keep = false; }, () => WorkCanEdit);
        AutomaticWorkMaximum = new(_ => { WorkAutomaticMaximum = true; workError = ""; NotifyPortWorkspace(); }, () => WorkCanEdit);
        FormatWorkMacs = new(_ => { try { WorkAddresses = string.Join(Environment.NewLine, PortMacList.Prepare(workBaseline!, WorkPort!.State.Name, [], WorkAddresses).Target.Select(b => MacRules.VendorAddress(b.Address, false))); } catch (ArgumentException ex) { workError = ex.Message; NotifyPortWorkspace(); } }, () => WorkCanEdit && WorkMacSupported);
        InitializeNetworkWorkspace();
    }
    private void SavePortForm()
    {
        if (workLoading || workReset || WorkPort is null || workBaseline is null) return;
        portForms[(workDevice, WorkPort.State.Name)] = new(ChangeEngine.ConfigurationHash(workBaseline), WorkName, WorkNetwork?.Value, WorkEnabled, WorkNewNetwork, WorkNetworkNumber, WorkAddresses, WorkExistingMacs.Where(m => m.Keep).Select(m => m.Binding.Address).ToArray(), WorkAutomaticMaximum, WorkMaximum, WorkViolation.Value, workRulesTouched, WorkTab);
    }
    private void BeginPortRefresh()
    { SavePortForm(); workRefreshPort = workDevice == SelectedDevice?.Id ? WorkPort?.State.Name : null; workLoading = true; }
    private void EndPortRefresh()
    {
        workPort = Ports.FirstOrDefault(p => p.State.Name == workRefreshPort); workLoading = false; Changed(nameof(WorkPort));
        LoadPortForm(); workReset = false; Changed(nameof(WorkPorts));
    }
    private void LoadPortForm()
    {
        workLoading = true; WorkExistingMacs.Clear(); workError = "";
        workBaseline = SelectedDevice is null ? null : workspace.GetState(SelectedDevice.Id); workDevice = SelectedDevice?.Id ?? Guid.Empty;
        if (WorkPort is not null && workBaseline is not null)
        {
            var key = (workDevice, WorkPort.State.Name);
            var old = !workReset && portForms.TryGetValue(key, out var saved) ? saved : null;
            if (old is not null && old.Baseline != ChangeEngine.ConfigurationHash(workBaseline)) { portForms.Remove(key); old = null; workNotice = "设备数据已更新，已按最新读取值重新显示；请重新核对本端口的填写。"; workNoticeFailed = false; }
            var policy = MacRules.Security(workBaseline).FirstOrDefault(s => s.PortName == WorkPort.State.Name);
            workName = old?.Name ?? WorkPort.State.Description; workNetwork = EasyNetworks.FirstOrDefault(n => n.Value == (old?.Network ?? WorkPort.State.Pvid)); workEnabled = old?.Enabled ?? WorkPort.State.Enabled;
            workNewNetwork = old?.NewNetwork ?? false; workNumber = old?.Number ?? ""; workAddresses = old?.Addresses ?? "";
            workAutomatic = old?.Automatic ?? true; workMaximum = old?.Maximum ?? Math.Max(1, MacRules.Bindings(workBaseline).Count(b => b.PortName == WorkPort.State.Name && b.Kind == MacBindingKind.SecureSource)).ToString();
            workViolation = MacViolations.FirstOrDefault(v => v.Value == (old?.Violation ?? policy?.Violation)) ?? MacViolations[0]; workRulesTouched = old?.RulesTouched ?? false; workTab = old?.Tab ?? (workReset ? workTab : 0);
            foreach (var binding in MacRules.Bindings(workBaseline).Where(b => b.PortName == WorkPort.State.Name && b.Kind == MacBindingKind.SecureSource))
            {
                var row = new KeptMac(binding) { Keep = old is null || old.Keep.Contains(binding.Address) };
                row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(KeptMac.Keep)) { row.NotifyDisposition(); WorkChanged(); } }; WorkExistingMacs.Add(row);
            }
        }
        workLoading = false;
        foreach (var name in new[] { nameof(WorkName), nameof(WorkNetwork), nameof(WorkEnabled), nameof(WorkNewNetwork), nameof(WorkNetworkNumber), nameof(WorkAddresses), nameof(WorkAutomaticMaximum), nameof(WorkMaximum), nameof(WorkViolation), nameof(WorkTab) }) Changed(name);
        UpdateReenteredMacs(); NotifyPortWorkspace();
    }
    private void WorkChanged()
    {
        if (workLoading) return;
        workError = ""; if (workReview) InvalidatePlan();
        UpdateReenteredMacs(); NotifyPortWorkspace();
    }
    private void UpdateReenteredMacs()
    {
        try { var entered = workBaseline is null || WorkPort is null ? [] : PortMacList.Prepare(workBaseline, WorkPort.State.Name, [], WorkAddresses).Target.Select(b => b.Address).ToHashSet(); foreach (var row in WorkExistingMacs) row.Reentered = entered.Contains(row.Binding.Address); }
        catch (ArgumentException) { foreach (var row in WorkExistingMacs) row.Reentered = false; }
    }
    public ChangeRequest[] BuildPortFormRequests()
    {
        if (!CanConfigure || WorkPort is null || workBaseline is null) throw new ArgumentException("请先连接并读取端口，再选择要修改的插口。");
        var current = workspace.GetState(workDevice) ?? throw new ArgumentException("设备数据不存在，请重新读取。");
        if (ChangeEngine.ConfigurationHash(current) != ChangeEngine.ConfigurationHash(workBaseline)) throw new ArgumentException("设备配置已经变化，请重新读取再编辑。");
        var port = WorkPort.State;
        if (WorkTab == 1)
        {
            if (!WorkMacSupported) throw new ArgumentException("此端口暂不支持图形化 MAC 接入限制；需要 Cisco 普通接入口。");
            var list = WorkMacList;
            var request = list.BuildRequest(port.Name, WorkAutomaticMaximum, WorkMaximum, WorkViolation.Value);
            var policy = MacRules.Security(workBaseline).FirstOrDefault(s => s.PortName == port.Name);
            if (list.Added.Length == 0 && list.Removed.Length == 0 && (!workRulesTouched || request.SecuritySettings?.FirstOrDefault() == (policy is null ? null : new PortSecuritySettings(port.Name, policy.Maximum, policy.Violation))))
                throw new ArgumentException("名单和规则都没有改变。要更换设备，请取消保留旧地址，再填写新地址。");
            return [request];
        }
        List<ChangeRequest> requests = [];
        var target = WorkNewNetwork ? ParseInt(WorkNetworkNumber, "新网络编号") : WorkNetwork?.Value ?? port.Pvid;
        if (target != port.Pvid) requests.Add(BeginnerOperations.MoveNetwork(workBaseline, [port.Name], target, WorkNewNetwork, true));
        var description = WorkName.Trim() != port.Description ? WorkName.Trim() : null;
        bool? enabled = WorkEnabled != port.Enabled ? WorkEnabled : null;
        if (description is not null || enabled is not null) requests.Add(new(Ports: [port.Name], Description: description, Enabled: enabled));
        if (requests.Count == 0) throw new ArgumentException("还没有修改端口信息。可以直接编辑名称、选择其他网络，或打开 / 关闭端口。");
        return requests.ToArray();
    }
    private void PreviewPortForm()
    {
        try
        {
            var requests = BuildPortFormRequests(); plan = engine.PreviewMany(RequireDevice(), requests);
            var after = ChangeEngine.ProjectBatch(RequireDevice(), workBaseline!, requests); var before = WorkPort!.State; var next = after.Ports.Single(p => p.Name == before.Name);
            List<EasyReviewRow> rows = [];
            if (WorkTab == 1)
            {
                var list = WorkMacList;
                WorkReviewSummary = $"{WorkPortTitle}：保留 {list.Kept.Length} 个原地址，新增 {list.Added.Length} 个，移除 {list.Removed.Length} 个；应用后共 {list.Target.Length} 个。端口会先关闭，完成后打开；验证通过后保存。";
                rows.Add(new("应用前的完整名单", list.Original.Length == 0 ? "没有静态安全绑定" : string.Join("\n", list.Original.Select(b => MacRules.VendorAddress(b.Address, false))), list.Target.Length == 0 ? "名单清空，解除 MAC 接入限制" : string.Join("\n", list.Target.Select(b => MacRules.VendorAddress(b.Address, false)))));
                var rule = MacRules.Security(after).FirstOrDefault(s => s.PortName == before.Name);
                rows.Add(new("设备接入规则", WorkCurrentRule, rule is null ? "关闭端口安全限制" : $"允许 {rule.Maximum} 个设备，{ChangeEngine.ViolationDescription(rule.Violation)}"));
            }
            else
            {
                WorkReviewSummary = $"只应用 {WorkPortTitle} 本页修改的端口信息。" + (before.Pvid != next.Pvid ? "更换网络可能暂时断网，已有静态安全绑定一起保留迁移。" : "");
                if (before.Description != next.Description) rows.Add(new("端口名称 / 说明", before.Description.Length == 0 ? "未填写" : before.Description, next.Description.Length == 0 ? "清空名称" : next.Description));
                if (before.Pvid != next.Pvid) rows.Add(new("所属网络", $"网络 {before.Pvid}", $"网络 {next.Pvid}（保留原有静态安全绑定）"));
            }
            if (before.Enabled != next.Enabled) rows.Add(new("端口开关", before.Enabled ? "打开" : "关闭", next.Enabled ? "打开" : "关闭，终端会断网"));
            WorkReviewRows = rows.ToArray(); workReview = true; workReviewIsPort = true; reviewedDraftIds.Clear(); reviewIncludesCurrent = true; LastResult = ""; workError = "";
            saveAfterVerify = true; PlanConfirmed = false; LiveRiskAccepted = false; NotifyPlan(); ReviewOpen = true; NotifyPortWorkspace();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { workError = ex.Message; NotifyPortWorkspace(); }
    }
    private void ResetPortFormAfterApply(bool wasWorkReview)
    { if (wasWorkReview && workReviewIsPort && WorkPort is not null) { portForms.Remove((workDevice, WorkPort.State.Name)); workReset = true; } }
    private void FinishPortForm(string message, bool failed, bool wasWorkReview)
    {
        if (!wasWorkReview) return;
        workNotice = message; workNoticeFailed = failed; workReview = false; if (failed) InvalidatePlan(); NotifyPortWorkspace();
    }
    private void NotifyPortWorkspace()
    {
        foreach (var name in new[] { nameof(WorkHasPort), nameof(WorkCanSelect), nameof(WorkCanEdit), nameof(WorkNeedsRead), nameof(WorkPortTitle), nameof(WorkPortCaption), nameof(WorkListCaption), nameof(WorkMacSummary), nameof(WorkLimitSummary), nameof(WorkCurrentRule), nameof(WorkManualMaximum), nameof(WorkNoExistingMacs), nameof(WorkOriginalMacHeading), nameof(WorkMacSupported), nameof(WorkError), nameof(WorkHasError), nameof(WorkNotice), nameof(WorkHasNotice), nameof(WorkNoticeColor), nameof(WorkReviewVisible), nameof(WorkReviewSummary), nameof(WorkReviewRows), nameof(ShowLegacyReview) }) Changed(name);
        foreach (var command in new[] { ReviewWorkPort, ResetWorkPort, KeepAllWorkMacs, ReplaceAllWorkMacs, AutomaticWorkMaximum, FormatWorkMacs }) command?.Refresh();
        Changed(nameof(WorkEditingMac));
        NotifyNetworkWorkspace();
    }
}
