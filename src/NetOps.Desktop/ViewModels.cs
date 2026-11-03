using System.Collections.ObjectModel;
using System.ComponentModel;
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
public sealed class ActionCommand(Action<object?> action) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action(parameter);
}
public sealed class PortItem(PortState state) : Observable
{
    public PortState State { get; } = state;
    private bool picked;
    public bool Picked { get => picked; set { if (Set(ref picked, value)) Changed(nameof(Fill)); } }
    public string Number => State.Name.Split('/').Last();
    public string Fill => Picked ? "#397A7B" : State.IsUplink ? "#554D79" : State.LinkUp ? "#173E3A" : "#203047";
    public string Detail => $"{State.Name} · {State.ModeLabel} · VLAN {State.Pvid} · {State.StatusLabel}";
    public override string ToString() => Detail;
}
public sealed record Choice<T>(T Value, string Label);

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly IWorkspace workspace;
    private readonly ISecretVault vault;
    private readonly ChangeEngine engine;
    private readonly IDriverRegistry drivers;
    private readonly IReadOnlyTransport transport;
    private readonly IAiClient ai;
    private CancellationTokenSource? cancellation;
    public MainViewModel(IWorkspace workspace, ISecretVault vault, IDriverRegistry drivers, IReadOnlyTransport transport, IAiClient ai)
    {
        this.workspace = workspace; this.vault = vault; this.drivers = drivers; this.transport = transport; this.ai = ai;
        engine = new(workspace, drivers);
        PreviewVlan = new(_ => Guard(() => SetPlan(engine.Preview(RequireDevice(), new(CreateVlanId: ParseInt(VlanId, "VLAN 编号"), VlanName: VlanName)))));
        PreviewPorts = new(_ => Guard(() => SetPlan(engine.Preview(RequireDevice(), new(Ports: Ports.Where(p => p.Picked).Select(p => p.State.Name).ToArray(), Mode: SelectedMode.Value,
            Pvid: ParseInt(Pvid, "PVID"), TaggedVlans: SelectedMode.Value == PortMode.Trunk ? ParseVlans(AllowedVlans) : [], Enabled: PortEnabled, Description: string.IsNullOrWhiteSpace(PortDescription) ? null : PortDescription.Trim())))));
        ApplyPlan = new(_ => Guard(() => { if (plan is null) throw new InvalidOperationException("请先生成预览。"); engine.ApplySimulation(plan.Id, PlanConfirmed); SetPlan(null); RefreshState(); Status = "模拟变更已完成，自动备份与审计已保存，回读验证通过。"; }));
        TogglePort = new(p => { if (p is PortItem port) port.Picked = !port.Picked; });
        SelectAllPorts = new(_ => { foreach (var port in Ports) port.Picked = !port.State.IsUplink; });
        Backup = new(_ => _ = RunAsync(BackupAsync));
        ReadVersion = new(_ => _ = RunAsync(ReadVersionAsync));
        AskAi = new(_ => _ = RunAsync(AskAiAsync));
        Cancel = new(_ => cancellation?.Cancel());
        CompareSnapshot = new(_ => Guard(Compare));
        Devices = new(workspace.GetDevices());
        Profiles = new(workspace.GetAiProfiles());
        SelectedProfile = Profiles.FirstOrDefault();
        SelectedDevice = Devices.FirstOrDefault();
    }
    public ObservableCollection<Device> Devices { get; }
    public ObservableCollection<PortItem> Ports { get; } = [];
    public ObservableCollection<VlanState> Vlans { get; } = [];
    public ObservableCollection<Snapshot> Snapshots { get; } = [];
    public ObservableCollection<AuditEntry> Audit { get; } = [];
    public ObservableCollection<AiProfile> Profiles { get; }
    public IReadOnlyList<DriverDescriptor> DriverDescriptors => drivers.Descriptors;
    public Choice<PortMode>[] Modes { get; } = [new(PortMode.Access, "接入模式"), new(PortMode.Trunk, "干道模式")];
    public Choice<AiProtocol>[] Protocols { get; } = [new(AiProtocol.Responses, "OpenAI 响应协议"), new(AiProtocol.ClaudeMessages, "Claude 消息协议"), new(AiProtocol.Gemini, "Gemini 内容生成协议"), new(AiProtocol.ChatCompletions, "对话补全兼容协议（DeepSeek / 通义 / 豆包等）"), new(AiProtocol.Ollama, "Ollama 本机协议")];
    public ActionCommand PreviewVlan { get; }
    public ActionCommand PreviewPorts { get; }
    public ActionCommand ApplyPlan { get; }
    public ActionCommand TogglePort { get; }
    public ActionCommand SelectAllPorts { get; }
    public ActionCommand Backup { get; }
    public ActionCommand ReadVersion { get; }
    public ActionCommand AskAi { get; }
    public ActionCommand Cancel { get; }
    public ActionCommand CompareSnapshot { get; }
    private Device? selectedDevice;
    public Device? SelectedDevice { get => selectedDevice; set { if (Set(ref selectedDevice, value)) { SetPlan(null); RefreshState(); SshOutput = "尚未连接真实设备。"; Changed(nameof(DeviceSummary)); } } }
    private string search = "";
    public string Search { get => search; set { Set(ref search, value); Changed(nameof(FilteredDevices)); } }
    public IEnumerable<Device> FilteredDevices => Devices.Where(d => $"{d.Name} {d.ManagementIp} {d.VendorLabel} {d.Location}".Contains(Search, StringComparison.OrdinalIgnoreCase));
    public string DeviceCount => Devices.Count.ToString();
    public string SimulationCount => Devices.Count(d => d.IsSimulation).ToString();
    public string ConnectedPorts => Ports.Count(p => p.State.LinkUp && p.State.Enabled).ToString();
    public string DeviceSummary => SelectedDevice is null ? "请选择设备" : $"{SelectedDevice.VendorLabel} · {SelectedDevice.Model} · {SelectedDevice.ManagementIp} · {SelectedDevice.ModeLabel}";
    public string SelectionLabel => $"已选 {Ports.Count(p => p.Picked)} 个端口";
    private string status = "本地工作区已就绪 · 示例数据来自模拟设备";
    public string Status { get => status; set => Set(ref status, value); }
    private bool busy;
    public bool Busy { get => busy; private set { Set(ref busy, value); Changed(nameof(Ready)); } }
    public bool Ready => !Busy;
    public string VlanId { get; set; } = "30";
    public string VlanName { get; set; } = "Guest";
    public string Pvid { get; set; } = "20";
    public string AllowedVlans { get; set; } = "1,10,20,99";
    public string PortDescription { get; set; } = "";
    public bool PortEnabled { get; set; } = true;
    private Choice<PortMode>? selectedMode;
    public Choice<PortMode> SelectedMode { get => selectedMode ?? Modes[0]; set => Set(ref selectedMode, value); }
    private ChangePlan? plan;
    public bool HasPlan => plan is not null;
    public PlanDiff[] PlanDifferences => plan?.Differences ?? [];
    public string PlanCommands => plan is null ? "生成预览后将在此显示厂商参考命令。" : string.Join(Environment.NewLine, plan.Commands);
    public string PlanRisks => plan is null ? "请选择端口或创建 VLAN，再生成变更预览。" : string.Join("\n", plan.Risks.Select(r => "• " + r));
    private bool planConfirmed;
    public bool PlanConfirmed { get => planConfirmed; set => Set(ref planConfirmed, value); }
    private Snapshot? selectedSnapshot;
    public Snapshot? SelectedSnapshot { get => selectedSnapshot; set { Set(ref selectedSnapshot, value); SnapshotText = value?.Content ?? "选择左侧备份以查看内容。"; } }
    private string snapshotText = "选择左侧备份以查看内容。";
    public string SnapshotText { get => snapshotText; set => Set(ref snapshotText, value); }
    private string sshOutput = "尚未连接真实设备。";
    public string SshOutput { get => sshOutput; set => Set(ref sshOutput, value); }
    private AiProfile? selectedProfile;
    public AiProfile? SelectedProfile { get => selectedProfile; set { if (Set(ref selectedProfile, value)) { ContextApproved = false; if (value is not null) { ProfileName = value.Name; ProfileUrl = value.BaseUrl; ProfileModel = value.Model; SelectedProtocol = Protocols.Single(p => p.Value == value.Protocol); Changed(nameof(ProfileName)); Changed(nameof(ProfileUrl)); Changed(nameof(ProfileModel)); } } } }
    public string ProfileName { get; set; } = "我的 DeepSeek";
    public string ProfileUrl { get; set; } = "https://api.deepseek.com";
    public string ProfileModel { get; set; } = "";
    private Choice<AiProtocol>? selectedProtocol;
    public Choice<AiProtocol> SelectedProtocol { get => selectedProtocol ?? Protocols[3]; set => Set(ref selectedProtocol, value); }
    public string AiQuestion { get; set; } = "办公终端能访问互联网，但无法访问服务器，应该先检查哪些项目？";
    private bool contextApproved;
    public bool ContextApproved { get => contextApproved; set => Set(ref contextApproved, value); }
    public string ContextPreview => SelectedDevice is null ? "请选择设备。" : AiContext.Build(SelectedDevice, workspace.GetState(SelectedDevice.Id));
    private string aiAnswer = "选择模型配置并核对待发送的上下文后，即可请求辅助排障。尚未调用任何 AI 服务。";
    public string AiAnswer { get => aiAnswer; set => Set(ref aiAnswer, value); }

    public void AddDevice(Device device, string username, string password)
    {
        Guard(() =>
        {
            device.Validate();
            if (!device.IsSimulation && (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password)))
            {
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) throw new ArgumentException("用户名与密码必须同时填写。");
                device = device with { CredentialReference = vault.Store(JsonSerializer.Serialize(new DeviceCredential(username, password))) };
            }
            DemoWorkspace.Add(workspace, device); Devices.Add(device); SelectedDevice = device;
            Changed(nameof(FilteredDevices)); Changed(nameof(DeviceCount)); Changed(nameof(SimulationCount));
            Status = "设备档案已保存。真实设备不会自动连接。";
        }, true);
    }
    public void SaveProfile(string apiKey, bool asNew)
    {
        Guard(() =>
        {
            var old = asNew ? null : SelectedProfile;
            var profile = new AiProfile(old?.Id ?? Guid.NewGuid(), ProfileName.Trim(), SelectedProtocol.Value, ProfileUrl.Trim(), ProfileModel.Trim(), old?.CredentialReference);
            AiClient.Validate(profile);
            if (!string.IsNullOrWhiteSpace(apiKey)) profile = profile with { CredentialReference = vault.Store(apiKey.Trim()) };
            if (profile.CredentialReference is null && profile.Protocol != AiProtocol.Ollama) throw new ArgumentException("请输入 API 密钥。已保存配置留空表示保留原密钥。");
            workspace.SaveAiProfile(profile);
            if (old is not null) Profiles.Remove(old);
            Profiles.Add(profile); SelectedProfile = profile; ContextApproved = false;
            if (old?.CredentialReference is not null && old.CredentialReference != profile.CredentialReference) vault.Remove(old.CredentialReference);
            Status = "AI 配置已保存，密钥使用 Windows 用户级加密保护。";
        }, true);
    }
    private Device RequireDevice() => SelectedDevice ?? throw new InvalidOperationException("请先选择设备。");
    private void RefreshState()
    {
        Ports.Clear(); Vlans.Clear(); Snapshots.Clear(); Audit.Clear(); SelectedSnapshot = null; ContextApproved = false;
        if (SelectedDevice is not null)
        {
            var state = workspace.GetState(SelectedDevice.Id);
            foreach (var p in state?.Ports ?? []) { var item = new PortItem(p); item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PortItem.Picked)) Changed(nameof(SelectionLabel)); }; Ports.Add(item); }
            foreach (var v in state?.Vlans ?? []) Vlans.Add(v);
            foreach (var s in workspace.GetSnapshots(SelectedDevice.Id)) Snapshots.Add(s);
            foreach (var a in workspace.GetAudit(SelectedDevice.Id)) Audit.Add(a);
        }
        Changed(nameof(ConnectedPorts)); Changed(nameof(SelectionLabel)); Changed(nameof(ContextPreview));
    }
    private void SetPlan(ChangePlan? value)
    {
        plan = value; PlanConfirmed = false;
        Changed(nameof(HasPlan)); Changed(nameof(PlanDifferences)); Changed(nameof(PlanCommands)); Changed(nameof(PlanRisks));
        if (value is not null) Status = $"预览已生成：{value.Differences.Length} 项差异，请在“变更预览”中审核。";
    }
    private async Task BackupAsync()
    {
        var device = RequireDevice();
        var content = device.IsSimulation ? JsonSerializer.Serialize(workspace.GetState(device.Id), ChangeEngine.JsonOptions) : await transport.ReadAsync(device, ReadCommand.RunningConfiguration, cancellation!.Token);
        workspace.AddSnapshot(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, device.IsSimulation ? "手动模拟备份" : "SSH 只读配置备份（exec）", content));
        workspace.AddAudit(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "配置备份", device.IsSimulation ? "模拟备份已加密保存" : "SSH 返回内容已加密保存；未做结构化语义验证"));
        RefreshState(); Status = "配置备份已加密保存，可在配置中心查看。";
    }
    private async Task ReadVersionAsync()
    {
        var device = RequireDevice();
        if (device.IsSimulation) { SshOutput = "当前为模拟设备，未建立 SSH 连接。请新增真实设备并填写可信主机指纹。"; return; }
        SshOutput = await transport.ReadAsync(device, ReadCommand.Version, cancellation!.Token);
        workspace.AddAudit(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "SSH 读取版本", "固定只读命令完成"));
        RefreshState(); Status = "SSH 只读命令已完成，版本结果显示在设备工作台。";
    }
    private async Task AskAiAsync()
    {
        var device = RequireDevice();
        var profile = SelectedProfile ?? throw new InvalidOperationException("请先在 AI 设置中保存配置并选择模型。");
        if (!ContextApproved) throw new InvalidOperationException("请先查看上下文预览，并勾选同意发送到当前 API 地址。");
        var context = AiContext.Build(device, workspace.GetState(device.Id));
        var question = AiQuestion;
        ContextApproved = false;
        AiAnswer = "正在请求辅助分析…";
        try
        {
            var answer = await ai.AnalyzeAsync(profile, question, context, cancellation!.Token);
            AiAnswer = $"AI 辅助建议 · {profile.Name}\n数据来源：{device.ModeLabel}\n\n{answer}";
            workspace.AddAudit(new(Guid.NewGuid(), device.Id, DateTimeOffset.UtcNow, "AI 辅助分析", "经用户确认发送白名单上下文；没有执行设备命令"));
            RefreshState(); Status = "AI 分析完成，结论仍需真实设备证据验证。";
        }
        catch { AiAnswer = "分析未完成，没有可展示的结果。请查看底部状态提示。"; throw; }
    }
    private void Compare()
    {
        var device = RequireDevice();
        var selected = SelectedSnapshot ?? throw new InvalidOperationException("请先选择一个备份。");
        if (!device.IsSimulation) throw new InvalidOperationException("真实设备差异比较需要重新采集配置，本阶段尚未提供自动比对。");
        var current = JsonSerializer.Serialize(workspace.GetState(device.Id), ChangeEngine.JsonOptions).Split('\n');
        var previous = selected.Content.Split('\n');
        SnapshotText = "逐行位置对比：− 为备份内容，+ 为当前内容；不代表厂商语义差异。\n\n" + string.Join("\n", Enumerable.Range(0, Math.Max(current.Length, previous.Length)).SelectMany(i =>
            i < current.Length && i < previous.Length && current[i] == previous[i] ? Array.Empty<string>() : new[] { i < previous.Length ? "− " + previous[i] : "", i < current.Length ? "+ " + current[i] : "" }));
    }
    private void Guard(Action action, bool rethrow = false)
    { try { action(); } catch (Exception ex) { Status = Error(ex); if (rethrow) throw new InvalidOperationException(Status); } }
    private async Task RunAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true; cancellation = new();
        try { await action(); }
        catch (OperationCanceledException) { Status = "操作已取消或请求超时。"; }
        catch (Exception ex) { Status = Error(ex); }
        finally { cancellation.Dispose(); cancellation = null; Busy = false; }
    }
    private static string Error(Exception ex) => ex is ArgumentException or InvalidOperationException or NotSupportedException ? ex.Message : "操作未完成，请检查连接参数、权限或接口协议。敏感异常详情未写入日志。";
    private static int ParseInt(string text, string name) => int.TryParse(text, out var value) ? value : throw new ArgumentException($"{name}必须为整数。");
    private static int[] ParseVlans(string text) => text.Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries).Select(s => ParseInt(s, "VLAN 编号")).Distinct().Order().ToArray();
    public void Dispose() { cancellation?.Cancel(); }
}
