using NetOps.Application;
using NetOps.Domain;

namespace NetOps.Desktop;

public enum EasyAction { PortName, PortNetwork, PortPower, AddMac, ReplaceMac, RemoveMac, CreateNetwork, RenameNetwork, DeleteNetwork, Backup }
public sealed record EasyReviewRow(string Object, string Before, string After);

public sealed partial class MainViewModel
{
    private bool easyActive, easyMultiple, easyClearName, easyCreateNetwork, easyKeepBindings = true, easyAutomaticMaximum = true, easyFailed, helpOpen;
    private int easyStep;
    private EasyAction easyAction;
    private string easySearch = "", easyName = "", easyNetworkNumber = "", easyNetworkName = "", easyAddresses = "", easyMaximum = "1", easyError = "", easyResult = "";
    private Choice<int>? easyNetwork;
    private Choice<bool>? easyPower;
    private Choice<MacBinding>? easyOldMac;
    private Choice<string>? easyViolation;
    public bool EasyActive { get => easyActive; private set { Set(ref easyActive, value); Changed(nameof(ShowLegacyReview)); NotifyTask(); NotifyEasy(); } }
    public bool ShowLegacyReview => ReviewOpen && !EasyActive;
    public bool CanOpenAdvancedTools => Ready && !ReviewOpen;
    public bool LegacyTaskContentVisible => TaskContentVisible && !EasyActive;
    public bool LegacyTaskReadVisible => NeedsTaskRead && !EasyActive;
    public bool LegacyTaskHeadingVisible => TaskFormVisible && !EasyActive;
    public int EasyStep { get => easyStep; private set { Set(ref easyStep, value); NotifyEasy(); } }
    public bool EasyConnectVisible => EasyStep == 0;
    public bool EasySelectVisible => EasyStep == 1;
    public bool EasyFillVisible => EasyStep == 2;
    public bool EasyReviewVisible => EasyStep == 3;
    public bool EasyResultVisible => EasyStep == 4;
    public bool EasyNeedsPort => easyAction is EasyAction.PortName or EasyAction.PortNetwork or EasyAction.PortPower or EasyAction.AddMac or EasyAction.ReplaceMac or EasyAction.RemoveMac;
    public bool EasyNameVisible => easyAction == EasyAction.PortName;
    public bool EasyNetworkVisible => easyAction == EasyAction.PortNetwork;
    public bool EasyPowerVisible => easyAction == EasyAction.PortPower;
    public bool EasyMacVisible => easyAction is EasyAction.AddMac or EasyAction.ReplaceMac or EasyAction.RemoveMac;
    public bool EasyNewMacVisible => easyAction is EasyAction.AddMac or EasyAction.ReplaceMac;
    public bool EasyOldMacVisible => easyAction is EasyAction.ReplaceMac or EasyAction.RemoveMac;
    public bool EasyNetworkManagementVisible => easyAction is EasyAction.CreateNetwork or EasyAction.RenameNetwork or EasyAction.DeleteNetwork;
    public bool EasyChooseNetworkVisible => easyAction is EasyAction.RenameNetwork or EasyAction.DeleteNetwork || EasyNetworkVisible && !EasyCreateNetwork;
    public bool EasyNetworkNumberVisible => easyAction == EasyAction.CreateNetwork || EasyNetworkVisible && EasyCreateNetwork;
    public bool EasyNetworkNameVisible => easyAction == EasyAction.RenameNetwork;
    public bool EasyDeleteNetworkVisible => easyAction == EasyAction.DeleteNetwork;
    public bool EasyBackupVisible => easyAction == EasyAction.Backup;
    public string EasyAgainLabel => EasyNeedsPort ? "继续设置另一个端口" : "再做一次同类操作";
    public bool EasyCanSelectMultiple => easyAction is EasyAction.PortName or EasyAction.PortNetwork or EasyAction.PortPower;
    public string EasyActionTitle => easyAction switch
    {
        EasyAction.PortName => "修改端口名称 / 说明", EasyAction.PortNetwork => "更换端口所属网络", EasyAction.PortPower => "打开或关闭端口",
        EasyAction.AddMac => "给端口绑定设备", EasyAction.ReplaceMac => "更换已经绑定的设备", EasyAction.RemoveMac => "取消设备绑定",
        EasyAction.CreateNetwork => "创建一个网络（VLAN）", EasyAction.RenameNetwork => "修改网络名称", EasyAction.DeleteNetwork => "删除不再使用的网络", _ => "备份交换机配置"
    };
    public string EasyStepTitle => EasyStep switch { 0 => "先连接交换机，读取当前设置", 1 => "选择要操作的端口", 2 => "填写这次要改成什么", 3 => "最后确认这次改动", _ => easyFailed ? "操作没有全部完成" : EasyResultWarning ? "配置已执行，仍有事项需要确认" : "操作已完成" };
    public string EasyProgress => EasyStep switch { 0 => "准备连接  →  选择对象  →  填写设置  →  确认执行", 1 => "第 1 步 / 3：选择端口", 2 => EasyNeedsPort ? "第 2 步 / 3：填写设置" : "第 1 步 / 2：填写设置", 3 => EasyNeedsPort ? "第 3 步 / 3：确认执行" : "第 2 步 / 2：确认执行", _ => "本次操作结果" };
    public string EasyNextLabel => EasyStep switch { 0 => Busy ? "正在连接…" : "连接并读取 →", 1 => "选好了，下一步 →", 2 => EasyBackupVisible ? "开始备份" : "查看改动，下一步 →", _ => "" };
    public bool EasyShowNext => EasyStep < 3;
    public bool EasyShowBack => EasyStep is 1 or 2 or 3;
    public string EasyBackLabel => EasyStep == 1 ? "返回操作菜单" : "← 上一步";
    public string EasyDeviceCaption => SelectedDevice is null ? "尚未选择设备" : $"{SelectedDevice.Name}  ·  {SelectedDevice.ManagementIp}  ·  {(SelectedDevice.IsSimulation ? "练习设备，不会修改真实网络" : "真实交换机")}";
    private DeviceState EasyState => SelectedDevice is not null ? workspace.GetState(SelectedDevice.Id) ?? throw new ArgumentException("请先连接并读取设备。") : throw new ArgumentException("请先选择设备。");
    public string EasySearch { get => easySearch; set { if (Set(ref easySearch, value)) Changed(nameof(EasyPorts)); } }
    public IEnumerable<PortItem> EasyPorts => Ports.Where(p => $"{p.State.Name} {p.State.Description} {p.State.Pvid}".Contains(EasySearch.Trim(), StringComparison.OrdinalIgnoreCase));
    public bool EasyMultiple { get => easyMultiple; set { if (Set(ref easyMultiple, value)) { if (!value) EnforceEasySelection(Ports.FirstOrDefault(p => p.Picked)); NotifyEasy(); } } }
    public string EasySelectionSummary => Ports.Any(p => p.Picked) ? $"已选择 {Ports.Count(p => p.Picked)} 个端口：" + string.Join("、", Ports.Where(p => p.Picked).Take(8).Select(p => p.FriendlyName)) : "还没有选择端口，请勾选列表左侧的小方框。";
    public EasyReviewRow[] EasyCurrentPorts => Ports.Where(p => p.Picked).Select(p => new EasyReviewRow(p.State.Name, string.IsNullOrWhiteSpace(p.State.Description) ? "没有名称" : p.State.Description,
        $"网络 {p.State.Pvid} · {(p.State.Enabled ? "已打开" : "已关闭")} · {(p.State.Mode == PortMode.Access ? "普通终端接入口" : p.State.ModeLabel)}")).ToArray();
    public string EasyName { get => easyName; set { if (Set(ref easyName, value)) EasyInputChanged(); } }
    public bool EasyClearName { get => easyClearName; set { if (Set(ref easyClearName, value)) EasyInputChanged(); } }
    public bool EasyCanEditName => !EasyClearName;
    public string EasyNetworkNumber { get => easyNetworkNumber; set { if (Set(ref easyNetworkNumber, value)) EasyInputChanged(); } }
    public string EasyNetworkName { get => easyNetworkName; set { if (Set(ref easyNetworkName, value)) EasyInputChanged(); } }
    public bool EasyCreateNetwork { get => easyCreateNetwork; set { if (Set(ref easyCreateNetwork, value)) EasyInputChanged(); } }
    public bool EasyKeepBindings { get => easyKeepBindings; set { if (Set(ref easyKeepBindings, value)) EasyInputChanged(); } }
    public Choice<int>[] EasyNetworks => SelectedDevice is null ? [] : (workspace.GetState(SelectedDevice.Id)?.Vlans ?? []).OrderBy(v => v.Id).Select(v => new Choice<int>(v.Id, $"网络 {v.Id} · {v.Name}")).ToArray();
    public Choice<int>? EasyNetwork { get => easyNetwork; set { if (Set(ref easyNetwork, value)) EasyInputChanged(); } }
    public Choice<bool>[] EasyPowerChoices { get; } = [new(true, "打开端口：允许连接设备联网"), new(false, "关闭端口：停止这个端口联网")];
    public Choice<bool>? EasyPower { get => easyPower; set { if (Set(ref easyPower, value)) EasyInputChanged(); } }
    public string EasyAddresses { get => easyAddresses; set { if (Set(ref easyAddresses, value)) EasyInputChanged(); } }
    public Choice<MacBinding>[] EasyExistingMacs => !Ports.Any(p => p.Picked) ? [] : MacRules.Bindings(EasyState).Where(b => b.Kind == MacBindingKind.SecureSource && b.PortName == Ports.First(p => p.Picked).State.Name).Select(b => new Choice<MacBinding>(b, b.Address)).ToArray();
    public Choice<MacBinding>? EasyOldMac { get => easyOldMac; set { if (Set(ref easyOldMac, value)) EasyInputChanged(); } }
    public bool EasyAutomaticMaximum
    {
        get => easyAutomaticMaximum;
        set
        {
            if (easyAutomaticMaximum && !value)
            {
                try { easyMaximum = (BuildEasyRequest().SecuritySettings?.FirstOrDefault()?.Maximum ?? 1).ToString(); Changed(nameof(EasyMaximum)); }
                catch (ArgumentException) { }
            }
            if (Set(ref easyAutomaticMaximum, value)) EasyInputChanged();
        }
    }
    public bool EasyManualMaximum => !EasyAutomaticMaximum;
    public string EasyMaximum { get => easyMaximum; set { if (Set(ref easyMaximum, value)) EasyInputChanged(); } }
    public Choice<string> EasyViolation { get => easyViolation ?? MacViolations[0]; set { if (Set(ref easyViolation, value)) EasyInputChanged(); } }
    public string EasyMacCount
    {
        get { try { var r = BuildEasyRequest(); var count = r.SecuritySettings?.FirstOrDefault()?.Maximum; return count is null ? "输入后会自动统计条数，并转换成交换机识别的格式。" : $"本端口设置完成后保留 {MacRules.TargetBindings(EasyState, r).Count(b => b.Kind == MacBindingKind.SecureSource && b.PortName == r.SecuritySettings![0].PortName)} 个绑定地址，允许上限为 {count}。"; } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return "输入后自动统计；支持 38-ff-59-06-1a-71、冒号、点号及连续 12 位。"; } }
    }
    public string EasyBindingNotice
    {
        get { if (SelectedDevice is null || workspace.GetState(SelectedDevice.Id) is not { } state) return "连接设备后显示当前绑定情况。"; var names = Ports.Where(p => p.Picked).Select(p => p.State.Name).ToArray(); var count = MacRules.Bindings(state).Count(b => b.Kind == MacBindingKind.SecureSource && names.Contains(b.PortName)); return count == 0 ? "所选端口没有需要迁移的静态安全绑定。" : $"这些端口已经绑定 {count} 个设备地址。勾选下方选项后，软件会把它们一起迁移到新网络，无需手动先删再绑。"; }
    }
    public string EasyError => easyError;
    public bool EasyHasError => easyError.Length > 0;
    public string EasyReviewSummary { get; private set; } = "";
    public EasyReviewRow[] EasyReviewRows
    {
        get
        {
            if (plan?.Request is { MigratePortSecurity: true } migration)
            {
                var networks = (migration.Ports ?? []).Select(name =>
                {
                    var port = Ports.Single(p => p.State.Name == name);
                    return new EasyReviewRow($"{port.FriendlyName}（{name}）", $"所属网络 {port.State.Pvid}", $"所属网络 {migration.Pvid}");
                });
                var addresses = (migration.RemoveMacBindings ?? []).Select(b => new EasyReviewRow($"保留设备地址 {b.Address}", $"{b.PortName}，网络 {b.VlanId}", $"同一端口，迁移到网络 {migration.Pvid}，保留绑定"));
                return networks.Concat(addresses).Concat(PlanDifferences.Where(d => d.Target.StartsWith("VLAN ")).Select(d => new EasyReviewRow(d.Target.Replace("VLAN", "网络"), d.Before, d.After))).ToArray();
            }
            return PlanDifferences.Select(d =>
            {
                var port = Ports.FirstOrDefault(p => p.State.Name == d.Target);
                if (port is null) return new EasyReviewRow(d.Target.Replace("VLAN", "网络"), d.Before, d.After);
                var title = $"{port.FriendlyName}（{port.State.Name}）";
                var request = plan?.Request;
                return easyAction switch
                {
                    EasyAction.PortName => new(title, string.IsNullOrEmpty(port.State.Description) ? "没有名称" : port.State.Description, string.IsNullOrEmpty(request?.Description) ? "清空名称" : request.Description),
                    EasyAction.PortNetwork => new(title, $"所属网络 {port.State.Pvid}", $"所属网络 {request?.Pvid}"),
                    _ => new EasyReviewRow(title, port.State.Enabled ? "端口已打开" : "端口已关闭", request?.Enabled == false ? "端口关闭" : "端口打开")
                };
            }).ToArray();
        }
    }
    public string EasyResult => easyResult;
    public bool EasyResultWarning => easyFailed || EasyResult.Contains("未确认") || EasyResult.Contains("尚未保存");
    public string EasyResultColor => easyFailed ? "#B42332" : EasyResultWarning ? "#9A650D" : "#146C50";
    public string EasyHint => EasyStep switch { 0 => "只读取设备信息，不会修改配置。连接失败时请检查地址、账户和连接设置。", 1 => EasySelectionSummary, 2 => "当前设置只是供你查看；只有这次填写的内容会进入确认页。", 3 => "核对设备与端口后确认。其他任务和批量草稿不会一起执行。", _ => "可以返回菜单继续操作，或重新读取设备核对结果。" };
    public bool HelpOpen { get => helpOpen; set => Set(ref helpOpen, value); }
    public string HelpTitle => EasyActive ? EasyActionTitle + "怎么操作" : Page switch { 0 => "怎样查看设备运行状态", 1 => "怎样看交换机之间的连接", 3 => "怎样处理异常提醒", _ => "第一次使用，从这里开始" };
    private string PageHelp => Page switch
    {
        0 => "这里用于观察，不会修改交换机。先选中一台设备，再看下方趋势；双击或点击查看可进入该设备。\n\n没有指标或采集过期不等于设备故障。需要先在设备的监控设置中配置采集账户，并检查采集时间。\n\n",
        1 => "这里显示设备之间的连接关系。点击设备看详情，展开堆叠可查看成员，连接详情可以定位接口。\n\n手工连接是你记录的关系；自动邻居来自设备读取。连线不代表实时网线状态或流量，先检查来源与更新时间。\n\n",
        3 => "先查看异常发生在哪台设备以及最近采集时间，再进入设备核对。确认提醒只代表已经看到，不会自动修复故障；设备恢复需等待新的采集结果。\n\n",
        _ => ""
    };
    public string HelpText => EasyActive ? $"{EasyHint}\n\n端口就是交换机上插网线的插口。\n\n端口名称：给插口写一个方便识别的说明，不改变联网方式。\n\n网络（VLAN）：把设备分到不同网络，编号应由网络管理员提供；更改后可能需要重新获取 IP。\n\n设备地址（MAC）：网卡的地址，用于限制这个插口允许哪些设备接入。\n\n连接信息和密码在“设备与连接设置”中填写。" : PageHelp + "1. 先添加一台交换机，填写它的管理地址与连接账户。\n\n2. 选择设备，再选择具体要做的事。每次只改一类设置。\n\n3. 按页面底部的下一步完成；看到确认页时仍未修改设备。\n\n4. 确认后软件先备份，再执行和检查结果。\n\n带“练习”标记的设备只改本机模拟数据，可以放心熟悉操作。\n\n命令终端、完整端口设置、AI 和批量草稿在高级功能中。";
    public ActionCommand StartEasy { get; private set; } = null!;
    public ActionCommand EasyNext { get; private set; } = null!;
    public ActionCommand EasyBack { get; private set; } = null!;
    public ActionCommand ExitEasy { get; private set; } = null!;
    public ActionCommand EasyAgain { get; private set; } = null!;
    public ActionCommand ToggleHelp { get; private set; } = null!;
    private void InitializeEasyWorkflow()
    {
        StartEasy = new(p => BeginEasy(Enum.Parse<EasyAction>(p?.ToString() ?? "PortName")), () => Ready && !ReviewOpen && SelectedDevice is not null);
        EasyNext = new(_ => AdvanceEasy(), () => Ready && EasyStep < 3 && (EasyStep != 1 || Ports.Any(p => p.Picked)));
        EasyBack = new(_ => { if (EasyStep == 3) { InvalidatePlan(); EasyStep = 2; } else if (EasyStep == 2 && EasyNeedsPort) EasyStep = 1; else LeaveEasy(); }, () => Ready);
        ExitEasy = new(_ => LeaveEasy(), () => Ready);
        EasyAgain = new(_ => BeginEasy(easyAction), () => Ready);
        ToggleHelp = new(_ => HelpOpen = !HelpOpen);
    }
    private void BeginEasy(EasyAction action)
    {
        InvalidatePlan(); easyAction = action; easyError = ""; easyResult = ""; easyFailed = false;
        easySearch = easyName = easyNetworkNumber = easyNetworkName = easyAddresses = ""; easyNetwork = null; easyOldMac = null; easyPower = null;
        easyMultiple = false; easyClearName = false; easyCreateNetwork = false; easyKeepBindings = true; easyAutomaticMaximum = true; easyMaximum = "1"; easyViolation = null;
        loading = true; foreach (var p in Ports) p.Picked = false; loading = false;
        EditorOpen = false; AiOpen = false; TaskLanding = false; EasyActive = true;
        saveAfterVerify = true;
        EasyStep = EasyBackupVisible ? 2 : !CanConfigure ? 0 : EasyNeedsPort ? 1 : 2;
        foreach (var name in new[] { nameof(EasySearch), nameof(EasyName), nameof(EasyNetworkNumber), nameof(EasyNetworkName), nameof(EasyAddresses), nameof(EasyNetwork), nameof(EasyOldMac), nameof(EasyPower), nameof(EasyMultiple), nameof(EasyClearName), nameof(EasyCreateNetwork), nameof(EasyKeepBindings), nameof(EasyAutomaticMaximum), nameof(EasyMaximum), nameof(EasyViolation) }) Changed(name);
        Changed(nameof(EasyPorts));
        NotifyEasy();
    }
    private void LeaveEasy() { InvalidatePlan(); EasyActive = false; TaskLanding = true; easyError = ""; }
    private void ResetEasy() { easyActive = false; easyError = ""; Changed(nameof(EasyActive)); Changed(nameof(ShowLegacyReview)); NotifyEasy(); }
    private void EnforceEasySelection(PortItem? changed)
    {
        if (!EasyActive || EasyStep != 1 || EasyMultiple && EasyCanSelectMultiple || changed?.Picked != true) return;
        loading = true; foreach (var p in Ports.Where(p => p != changed)) p.Picked = false; loading = false;
    }
    private void EasyInputChanged() { if (EasyActive && EasyStep == 3) { InvalidatePlan(); easyStep = 2; } easyError = ""; NotifyEasy(); }
    private void AdvanceEasy()
    {
        easyError = "";
        if (EasyStep == 0)
        {
            _ = RunAsync("连接并读取设备", async () => { await ReadCurrentAsync(); if (!CanConfigure) throw new InvalidOperationException(CapabilityHint); EasyStep = EasyNeedsPort ? 1 : 2; });
            return;
        }
        try
        {
            if (EasyStep == 1)
            {
                if (EasyMacVisible && Ports.Count(p => p.Picked) != 1) throw new ArgumentException("绑定设备时一次选择一个端口；同一端口可以输入多个设备地址。");
                if (EasyMacVisible && SelectedDevice?.Vendor != Vendor.Cisco) throw new ArgumentException("这个品牌暂未适配设备接入限制。可在高级 MAC 设置中查看静态转发功能，两者用途不同。");
                var name = Ports.FirstOrDefault(p => p.Picked)?.State.Name;
                var policy = SelectedDevice is null ? null : MacRules.Security(EasyState).SingleOrDefault(p => p.PortName == name);
                easyViolation = MacViolations.FirstOrDefault(v => v.Value == policy?.Violation); easyMaximum = (policy?.Maximum ?? 1).ToString();
                if (EasyOldMacVisible && EasyExistingMacs.Length == 1) easyOldMac = EasyExistingMacs[0];
                Changed(nameof(EasyOldMac)); Changed(nameof(EasyViolation)); Changed(nameof(EasyMaximum)); EasyStep = 2; return;
            }
            if (EasyBackupVisible) { _ = RunAsync("备份当前配置", async () => { await BackupAsync(); FinishEasyOperation(Status, false); }); return; }
            var request = BuildEasyRequest();
            plan = engine.Preview(RequireDevice(), request); reviewedDraftIds.Clear(); reviewIncludesCurrent = true; PlanConfirmed = false; LiveRiskAccepted = false;
            if (EasyNewMacVisible) { easyAddresses = string.Join(Environment.NewLine, ParseEasyMacs()); Changed(nameof(EasyAddresses)); }
            EasyReviewSummary = DescribeEasyRequest(request); Changed(nameof(EasyReviewSummary)); NotifyPlan(); ReviewOpen = true; EasyStep = 3;
        }
        catch (Exception ex) { easyError = Error(ex); NotifyEasy(); }
    }
    private string[] ParseEasyMacs()
    {
        var lines = EasyAddresses.Split(['\r', '\n', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0) throw new ArgumentException("请填写至少一个设备地址，每行一个。");
        if (lines.Length > 64) throw new ArgumentException("本版一个端口最多编辑 64 个设备地址。");
        return lines.Select((line, i) => { try { return MacRules.Normalize(line); } catch (ArgumentException ex) { throw new ArgumentException($"第 {i + 1} 条地址不正确：{ex.Message}"); } }).Distinct().ToArray();
    }
    public ChangeRequest BuildEasyRequest()
    {
        var state = EasyState; var names = Ports.Where(p => p.Picked).Select(p => p.State.Name).ToArray();
        if (EasyNeedsPort && names.Length == 0) throw new ArgumentException("请先选择一个端口。");
        if (EasyNameVisible)
        {
            if (!EasyClearName && string.IsNullOrWhiteSpace(EasyName)) throw new ArgumentException("请填写新的名称；如果要清除旧名称，请勾选“清空原有名称”。");
            return new(Ports: names, Description: EasyClearName ? "" : EasyName.Trim());
        }
        if (EasyNetworkVisible)
        {
            var vlan = EasyCreateNetwork ? ParseInt(EasyNetworkNumber, "新网络编号") : EasyNetwork?.Value ?? throw new ArgumentException("请选择要切换到的网络。");
            return BeginnerOperations.MoveNetwork(state, names, vlan, EasyCreateNetwork, EasyKeepBindings);
        }
        if (EasyPowerVisible) return new(Ports: names, Enabled: EasyPower?.Value ?? throw new ArgumentException("请选择打开还是关闭端口。"));
        if (EasyNetworkManagementVisible)
        {
            if (easyAction == EasyAction.CreateNetwork) { var id = ParseInt(EasyNetworkNumber, "网络编号"); return new(CreateVlanId: id, VlanName: $"VLAN{id}"); }
            var id2 = EasyNetwork?.Value ?? throw new ArgumentException("请选择要操作的网络。");
            return easyAction == EasyAction.DeleteNetwork ? new(DeleteVlanId: id2) : new(RenameVlanId: id2, VlanName: EasyNetworkName.Trim());
        }
        if (!EasyMacVisible) throw new ArgumentException("请先选择具体操作。");
        if (names.Length != 1) throw new ArgumentException("绑定设备时请选择一个端口。");
        var port = state.Ports.Single(p => p.Name == names[0]);
        var old = EasyOldMacVisible ? EasyOldMac?.Value : null;
        if (EasyOldMacVisible && (old is null || !MacRules.Bindings(state).Contains(old) || old.PortName != port.Name)) throw new ArgumentException("请选择当前端口中要更换或取消绑定的原设备地址。");
        if (easyAction == EasyAction.RemoveMac) return new(RemoveMacBindings: [old!]);
        var mapped = ParseEasyMacs().Select(a => new MacBinding(a, port.Pvid, port.Name, MacBindingKind.SecureSource)).ToArray();
        var remove = old is not null && !mapped.Contains(old) ? new[] { old } : [];
        var add = mapped.Where(b => !MacRules.Bindings(state).Contains(b)).ToArray();
        var target = MacRules.Bindings(state).Except(remove).Concat(add).ToArray(); var count = target.Count(b => b.Kind == MacBindingKind.SecureSource && b.PortName == port.Name);
        return new(AddMacBindings: add, RemoveMacBindings: remove, SecuritySettings: [new(port.Name, EasyAutomaticMaximum ? count : ParseInt(EasyMaximum, "允许设备数量"), EasyViolation.Value)]);
    }
    private string DescribeEasyRequest(ChangeRequest r)
    {
        var ports = r.Ports ?? MacRules.SecurityChanges(r); var targets = string.Join("、", ports);
        return easyAction switch
        {
            EasyAction.PortName => $"为 {ports.Length} 个端口{(EasyClearName ? "清空名称" : $"设置名称“{EasyName.Trim()}”")}。所属网络、接入模式和启停状态都不改变。",
            EasyAction.PortNetwork => $"把 {ports.Length} 个端口改到网络 {r.Pvid}。" + (r.MigratePortSecurity ? "原有静态设备绑定一起迁移，数量上限、违规动作和启停状态保持原值。" : "已有名称和启停状态保持原值。"),
            EasyAction.PortPower => $"{(r.Enabled == true ? "打开" : "关闭")} {ports.Length} 个端口。" + (r.Enabled == false ? "这些端口连接的设备将无法通过它们联网。" : "打开后仍需网线、终端及网络配置正常才能通信。"),
            EasyAction.AddMac or EasyAction.ReplaceMac or EasyAction.RemoveMac => $"端口 {targets}：新增 {r.AddMacBindings?.Length ?? 0} 个地址，移除 {r.RemoveMacBindings?.Length ?? 0} 个地址。操作期间会暂时关闭端口，完成后打开，验证通过后保存。",
            _ => $"{EasyActionTitle}。请核对下面的网络编号与前后变化。"
        };
    }
    private void FinishEasyOperation(string message, bool failed)
    {
        if (!EasyActive) return;
        if (EasyBackupVisible && !failed) message = "配置备份已保存到本机。返回操作菜单，点击“查看备份与操作记录”即可找到。";
        easyFailed = failed; easyResult = message; easyError = ""; EasyStep = 4; NotifyEasy();
    }
    private void NotifyEasy()
    {
        foreach (var name in new[] { nameof(LegacyTaskContentVisible), nameof(LegacyTaskReadVisible), nameof(LegacyTaskHeadingVisible), nameof(EasyStep), nameof(EasyConnectVisible), nameof(EasySelectVisible), nameof(EasyFillVisible), nameof(EasyReviewVisible), nameof(EasyResultVisible), nameof(EasyActionTitle), nameof(EasyStepTitle), nameof(EasyProgress), nameof(EasyNextLabel), nameof(EasyShowNext), nameof(EasyShowBack), nameof(EasyBackLabel), nameof(EasyDeviceCaption), nameof(EasySelectionSummary), nameof(EasyCurrentPorts), nameof(EasyCanSelectMultiple), nameof(EasyNameVisible), nameof(EasyNetworkVisible), nameof(EasyPowerVisible), nameof(EasyMacVisible), nameof(EasyNewMacVisible), nameof(EasyOldMacVisible), nameof(EasyNetworkManagementVisible), nameof(EasyChooseNetworkVisible), nameof(EasyNetworkNumberVisible), nameof(EasyNetworkNameVisible), nameof(EasyDeleteNetworkVisible), nameof(EasyBackupVisible), nameof(EasyCanEditName), nameof(EasyNetworks), nameof(EasyExistingMacs), nameof(EasyManualMaximum), nameof(EasyMacCount), nameof(EasyBindingNotice), nameof(EasyError), nameof(EasyHasError), nameof(EasyReviewRows), nameof(EasyResult), nameof(EasyResultWarning), nameof(EasyResultColor), nameof(EasyHint), nameof(HelpTitle), nameof(HelpText) }) Changed(name);
        Changed(nameof(EasyAgainLabel)); Changed(nameof(CanOpenAdvancedTools));
        foreach (var command in new[] { StartEasy, EasyNext, EasyBack, ExitEasy, EasyAgain, ToggleHelp }) command?.Refresh();
    }
}
