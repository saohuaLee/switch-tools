using NetOps.Application;
using NetOps.Domain;

namespace NetOps.Desktop;

public sealed partial class MainViewModel
{
    public bool IsSecureMac => SelectedMacKind.Value == MacBindingKind.SecureSource;
    public bool HasMacReplacements => MacRows.Any(r => r.OriginalBinding is not null);
    public bool HasMacRows => MacRows.Count > 0;
    public Choice<string>[] MacViolations { get; } = [new("shutdown", "关闭违规端口（shutdown，默认）"), new("restrict", "丢弃违规报文并告警（restrict）"), new("protect", "仅丢弃违规报文（protect）")];
    private Choice<string>? selectedMacViolation;
    public Choice<string> SelectedMacViolation { get => selectedMacViolation ?? MacViolations[0]; set { if (Set(ref selectedMacViolation, value)) { Changed(nameof(MacViolationHelp)); MacEditorChanged(); } } }
    public string MacViolationHelp => SelectedMacViolation.Value switch
    {
        "shutdown" => "发现违规源 MAC 后，整个端口进入错误禁用状态，合法终端也会断网，需要恢复端口。",
        "restrict" => "丢弃违规源 MAC 的报文，记录日志及违规计数，支持发送告警；端口保持运行。",
        _ => "仅丢弃违规源 MAC 的报文，不发送此类违规通知，不增加违规计数；端口保持运行。"
    };
    private bool macAutomaticMaximum = true;
    private string macMaximum = "1", macPastePort = "", macPasteText = "", macEntryError = "";
    public bool MacAutomaticMaximum
    {
        get => macAutomaticMaximum;
        set
        {
            var current = MacMaximum;
            if (!Set(ref macAutomaticMaximum, value)) return;
            if (!value) macMaximum = current;
            Changed(nameof(MacManualMaximum)); MacEditorChanged();
        }
    }
    public bool MacManualMaximum => !MacAutomaticMaximum;
    public string MacMaximum
    {
        get
        {
            if (!MacAutomaticMaximum) return macMaximum;
            try { return (BuildMacRequest().SecuritySettings ?? []).Select(s => s.Maximum).DefaultIfEmpty(1).Max().ToString(); }
            catch (ArgumentException) { return "1"; }
        }
        set { if (Set(ref macMaximum, value)) MacEditorChanged(); }
    }
    public string MacCountSummary
    {
        get
        {
            try
            {
                var request = BuildMacRequest();
                if (!IsSecureMac) return $"草稿 {MacRows.Count} 条；静态转发只指定目的 MAC 的出口，不限制源 MAC 接入。";
                var target = MacRules.TargetBindings(ProjectedState()!, request);
                return string.Join("；", (request.SecuritySettings ?? []).Select(s => $"{s.PortName}：最终 {target.Count(b => b.Kind == MacBindingKind.SecureSource && b.PortName == s.PortName)} 条，上限 {s.Maximum}"));
            }
            catch (ArgumentException) { return $"已添加 {MacRows.Count} 行；可向同一端口添加多条 MAC，填完后按端口统计最终数量。"; }
        }
    }
    public string MacPastePort
    {
        get => macPastePort;
        set { if (value is not null && Set(ref macPastePort, value)) { foreach (var item in MacBindings) item.Picked = false; Changed(nameof(MacVisibleBindings)); MacEditorChanged(); } }
    }
    public string MacPasteText { get => macPasteText; set { if (Set(ref macPasteText, value)) MacEditorChanged(); } }
    public string MacInputMessage => macEntryError.Length > 0 ? macEntryError : MacRows.Count == 0 && string.IsNullOrWhiteSpace(MacPasteText) ? "填写 MAC 后点击“下一步”；也可以先从当前绑定带入旧地址进行更换。" : MacValidation.Length > 0 ? MacValidation : "地址与参数已通过校验，可以审核本次配置。";
    private void RunMacEditorAction(Action action)
    { try { macEntryError = ""; action(); } catch (Exception ex) { macEntryError = Error(ex); Status = macEntryError; } Changed(nameof(MacInputMessage)); }
    private void MacEditorChanged()
    {
        InvalidatePlan();
        macEntryError = "";
        foreach (var name in new[] { nameof(MacValidation), nameof(MacMaximum), nameof(MacCountSummary), nameof(MacInputMessage), nameof(MacVisibleBindings), nameof(HasMacVisibleBindings), nameof(HasMacReplacements), nameof(HasMacRows) }) Changed(name);
        RefreshCommands(); RefreshWorkbenchCommands();
    }
    public ChangeRequest BuildMacRequest()
    {
        var state = ProjectedState() ?? (SelectedDevice is not null ? workspace.GetState(SelectedDevice.Id) : null);
        if (state is null) throw new ArgumentException("请先读取当前设备配置。");
        if (MacRows.Count == 0 && string.IsNullOrWhiteSpace(MacBatch) && string.IsNullOrWhiteSpace(MacPasteText)) throw new ArgumentException("请填写 MAC 地址，或从当前绑定带入需要更换的旧地址。");
        var mapped = (MacRows.Count > 0 ? MacRows.Select(r => MacRules.Canonical(new(r.Address, ParseInt(r.Vlan, "VLAN"), r.PortName, SelectedMacKind.Value))) : !string.IsNullOrWhiteSpace(MacBatch) ? MacRules.ParseBatch(MacBatch, SelectedMacKind.Value) : [])
            .Concat(string.IsNullOrWhiteSpace(MacPasteText) ? [] : ParseMacPaste(state)).ToArray();
        if (MacRows.Any(r => r.OriginalBinding is not null && r.OriginalBinding.Kind != SelectedMacKind.Value)) throw new ArgumentException("草稿中的原绑定与当前绑定用途不同，请切回原用途或移除对应草稿。");
        var originals = MacRows.Where(r => r.OriginalBinding is not null).Select(r => r.OriginalBinding!).ToArray();
        var remove = originals.Where(b => !mapped.Contains(b)).ToArray();
        var add = mapped.Where(b => !originals.Contains(b)).ToArray();
        if (mapped.Distinct().Count() != mapped.Length || originals.Distinct().Count() != originals.Length) throw new ArgumentException("草稿存在重复 MAC 或重复的原绑定，请移除重复行。");
        if (!IsSecureMac) return new(AddMacBindings: add, RemoveMacBindings: remove);
        var target = MacRules.Bindings(state).Except(remove).Concat(add).ToArray();
        var policies = mapped.Concat(originals).Select(b => b.PortName).Distinct().Select(name =>
        {
            var count = target.Count(b => b.Kind == MacBindingKind.SecureSource && b.PortName == name);
            return new PortSecuritySettings(name, MacAutomaticMaximum ? count : ParseInt(macMaximum, "MAC 数量上限"), SelectedMacViolation.Value);
        }).Where(s => target.Any(b => b.Kind == MacBindingKind.SecureSource && b.PortName == s.PortName)).ToArray();
        return new(AddMacBindings: add, RemoveMacBindings: remove, SecuritySettings: policies);
    }
    private void LoadSelectedMac()
    {
        if (!string.IsNullOrWhiteSpace(MacPasteText)) throw new ArgumentException("请先完成或清空右侧的新增 MAC 输入，再更换已有绑定；新增输入不会被自动丢弃。");
        var items = MacBindings.Where(m => m.Picked).ToArray();
        if (items.Select(m => m.Binding.Kind).Distinct().Count() != 1) throw new ArgumentException("请勾选同一种用途的绑定后再更换或调整。");
        var kind = items[0].Binding.Kind;
        if (MacRows.Count > 0 && SelectedMacKind.Value != kind) throw new ArgumentException("请先提交或移除当前草稿，再编辑另一种用途的绑定。");
        SelectedMacKind = MacKinds.Single(k => k.Value == kind);
        var violations = items.Select(m => m.Security?.Violation ?? "shutdown").Distinct().ToArray();
        if (violations.Length == 1) SelectedMacViolation = MacViolations.FirstOrDefault(v => v.Value == violations[0]) ?? MacViolations[0];
        foreach (var item in items.Where(m => !MacRows.Any(r => r.OriginalBinding == m.Binding)))
        { AddRow(item.Binding.PortName, item.Binding.VlanId.ToString(), item.Binding); MacRows[^1].Address = item.Binding.Address; }
        Status = "原 MAC 已带入草稿：修改“新 MAC”完成换绑，也可保留 MAC 并调整端口安全参数；同一端口的换绑一次关口完成。";
    }
    public ChangeRequest BuildDeleteMacRequest()
    {
        var state = ProjectedState() ?? throw new ArgumentException("请先读取当前设备配置。");
        var remove = MacBindings.Where(m => m.Picked).Select(m => m.Binding).ToArray();
        var target = MacRules.Bindings(state).Except(remove).ToArray();
        var policies = remove.Where(b => b.Kind == MacBindingKind.SecureSource).Select(b => b.PortName).Distinct()
            .Where(name => target.Any(b => b.PortName == name && b.Kind == MacBindingKind.SecureSource))
            .Select(name => new PortSecuritySettings(name, MacAutomaticMaximum ? target.Count(b => b.PortName == name && b.Kind == MacBindingKind.SecureSource) : ParseInt(macMaximum, "MAC 数量上限"),
                MacRules.Security(state).SingleOrDefault(s => s.PortName == name)?.Violation ?? "shutdown")).ToArray();
        return new(RemoveMacBindings: remove, SecuritySettings: policies);
    }
    public void NormalizeMacRow(MacDraftRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Address)) return;
        try { row.Address = MacRules.Normalize(row.Address); }
        catch (ArgumentException) { /* 保留原输入，由行下方校验解释错误，不能猜测缺失地址。 */ }
    }
    public void NormalizeMacDrafts()
    {
        var addresses = MacRows.Select(r => MacRules.Normalize(r.Address)).ToArray();
        for (var i = 0; i < MacRows.Count; i++) MacRows[i].Address = addresses[i];
    }
    private MacBinding[] ParseMacPaste(DeviceState state)
    {
        var lines = MacPasteText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length + MacRows.Count > 1000) throw new ArgumentException("单次草稿最多 1000 行。");
        var rows = lines.Select(line =>
        {
            var fields = line.Split([',', '，']);
            if (fields.Length == 3) return MacRules.Canonical(new(fields[0], ParseInt(fields[1].Trim(), "VLAN"), fields[2].Trim(), SelectedMacKind.Value));
            if (fields.Length != 1) throw new ArgumentException("每行填写一个 MAC，或 MAC,VLAN,完整端口名称。");
            var port = state.Ports.SingleOrDefault(p => p.Name == MacPastePort) ?? throw new ArgumentException("只粘贴 MAC 时，请先选择批量导入的目标端口。");
            return new MacBinding(MacRules.Normalize(line), port.Pvid, port.Name, SelectedMacKind.Value);
        }).ToArray();
        if (rows.Length == 0) throw new ArgumentException("请先粘贴 MAC 地址。");
        if (rows.GroupBy(MacRules.Key).Any(g => g.Count() > 1) || rows.Any(b => MacRows.Any(r => TrySameMac(r, b)))) throw new ArgumentException("粘贴内容或现有草稿中存在重复 MAC / VLAN，请去重后导入。");
        return rows;
    }
    private void ImportMacDrafts()
    {
        var rows = ParseMacPaste(ProjectedState() ?? throw new ArgumentException("请先读取当前设备配置。"));
        // 先清空输入，避免添加每行时与尚未导入的输入重复计算。
        MacPasteText = "";
        foreach (var row in rows) { AddRow(row.PortName, row.VlanId.ToString()); MacRows[^1].Address = row.Address; }
        MacPasteText = ""; Status = $"已导入 {rows.Length} 条 MAC 并整理格式，尚未提交或下发。";
    }
    private static bool TrySameMac(MacDraftRow row, MacBinding binding)
    { try { return MacRules.Normalize(row.Address) == binding.Address && int.TryParse(row.Vlan, out var vlan) && vlan == binding.VlanId; } catch (ArgumentException) { return false; } }
}
