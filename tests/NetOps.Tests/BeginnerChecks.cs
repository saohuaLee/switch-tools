using System.IO;
using System.Net.Http;
using NetOps.Application;
using NetOps.Desktop;
using NetOps.Domain;
using NetOps.Infrastructure;

public static class BeginnerChecks
{
    private const string P1 = "GigabitEthernet1/0/1", P2 = "GigabitEthernet1/0/2";
    private const string Raw = "vlan 10\n name Office\n!\nvlan 20\n name Guest\n!\ninterface GigabitEthernet1/0/1\n description Old\n switchport mode access\n switchport access vlan 10\n!\ninterface GigabitEthernet1/0/2\n description Keep\n switchport mode access\n switchport access vlan 10\n shutdown\n!\nend\n";
    private static void Check(bool value, string why = "断言失败") { if (!value) throw new InvalidOperationException(why); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("应拒绝此请求"); }
    private static async Task Finish(MainViewModel vm) { for (var i = 0; i < 300 && vm.Busy; i++) await Task.Delay(10); Check(!vm.Busy, "操作未按时结束"); }
    public static async Task Run(string root, Action<string, Action> test, Func<string, Func<Task>, Task> asyncTest)
    {
        var db = new LocalWorkspace(Path.Combine(root, "beginner.db")); DemoWorkspace.Seed(db); var vault = new DpapiVault(Path.Combine(root, "beginner-vault"));
        var drivers = new DriverRegistry(); var sessions = new NoSessions(); using var http = new HttpClient();
        Device Create(bool secure = false, bool down = false)
        {
            var d = db.GetDevices()[0] with { Id = Guid.NewGuid(), Name = "新手流程测试", Vendor = Vendor.Cisco, DriverId = "cisco.iosxe", IsSimulation = true };
            db.SaveDevice(d); var state = ConfigurationParser.Parse(d, Raw);
            if (secure) state = state with { MacBindings = [new("02:11:22:33:44:55", 10, P1, MacBindingKind.SecureSource)], PortSecurity = [new(P1, true, 2, "restrict")], Ports = state.Ports.Select(p => p.Name == P1 ? p with { Enabled = !down } : p).ToArray() };
            db.SaveState(state); return d;
        }
        MainViewModel Vm(Device d, bool read = true)
        {
            var vm = new MainViewModel(db, vault, drivers, new NoAi(), sessions, new NetworkOperations(db, drivers, sessions, vault, http)); vm.SelectedDevice = vm.Devices.Single(v => v.Id == d.Id); vm.OpenDevice.Execute(d.Id); if (read) vm.ReadCurrent.Execute(null); return vm;
        }
        void Select(MainViewModel vm, EasyAction kind) { vm.StartEasy.Execute(kind.ToString()); vm.Ports.Single(p => p.State.Name == P1).Picked = true; vm.EasyNext.Execute(null); Check(vm.EasyStep == 2); }
        test("截图回归：已绑定端口仅改中文名称，不附带未改变的模式 VLAN 或启停", () =>
        {
            var d = Create(true); using var vm = Vm(d); vm.Ports[0].Picked = true;
            Check(!vm.Ports[0].FriendlyName.Contains("交换机 1"), "未读取成员信息时不能仅凭接口编号推测堆叠归属");
            vm.ApplyMode = vm.ApplyPvid = vm.ApplyEnabled = true; vm.SelectedMode = vm.Modes[0]; vm.Pvid = "10"; vm.PortEnabled = true;
            Check(vm.ModifiedFieldCount == 0); vm.PortDescription = "财务室打印机"; var r = vm.BuildPortRequest();
            Check(vm.ModifiedFieldCount == 1 && r.Mode is null && r.Pvid is null && r.Enabled is null && vm.CanPreviewPorts);
            var plan = new ChangeEngine(db, drivers).Preview(d, r); Check(plan.Commands.Contains("description 财务室打印机") && plan.Commands.All(c => !c.Contains("switchport") && c != "shutdown"));
        });
        await asyncTest("首次使用分步连接，读取后进入端口选择，不修改真实网络", async () =>
        {
            var d = Create(); using var vm = Vm(d, false); vm.StartEasy.Execute("PortName"); Check(vm.EasyConnectVisible && !vm.ReviewOpen);
            var before = ChangeEngine.ConfigurationHash(db.GetState(d.Id)!); vm.EasyNext.Execute(null); await Finish(vm);
            Check(vm.EasySelectVisible && vm.CanConfigure && ChangeEngine.ConfigurationHash(db.GetState(d.Id)!) == before);
        });
        test("端口默认单选，明确开启批量后才能同时选多口", () =>
        {
            using var vm = Vm(Create()); vm.StartEasy.Execute("PortName"); vm.Ports[0].Picked = true; vm.Ports[1].Picked = true;
            Check(vm.Ports.Count(p => p.Picked) == 1 && vm.Ports[1].Picked);
            vm.EasyMultiple = true; vm.Ports[0].Picked = true; Check(vm.Ports.Count(p => p.Picked) == 2); vm.EasyNext.Execute(null);
            vm.EasyName = "批量名称"; var r = vm.BuildEasyRequest(); Check(r.Ports!.Length == 2 && r.Description == "批量名称" && r.Pvid is null && r.Enabled is null);
        });
        test("返回上一步保留输入，退出不新增草稿也不改设备", () =>
        {
            var d = Create(); using var vm = Vm(d); var before = ChangeEngine.ConfigurationHash(db.GetState(d.Id)!); Select(vm, EasyAction.PortName); vm.EasyName = "会议室插口";
            vm.EasyBack.Execute(null); Check(vm.EasySelectVisible && vm.EasyName == "会议室插口"); vm.EasyNext.Execute(null); vm.EasyNext.Execute(null);
            Check(vm.EasyReviewVisible && vm.ReviewOpen && !vm.ShowLegacyReview); vm.EasyBack.Execute(null); Check(vm.EasyFillVisible && vm.EasyName == "会议室插口");
            vm.ExitEasy.Execute(null); Check(!vm.EasyActive && vm.TaskLanding && vm.PendingChanges.Count == 0 && ChangeEngine.ConfigurationHash(db.GetState(d.Id)!) == before);
        });
        test("空名称不能意外清除，清空需要明确选择，非法输入留在填写步骤", () =>
        {
            using var vm = Vm(Create()); Select(vm, EasyAction.PortName); vm.EasyNext.Execute(null); Check(vm.EasyFillVisible && vm.EasyHasError && !vm.ReviewOpen);
            vm.EasyClearName = true; Check(vm.BuildEasyRequest().Description == ""); vm.EasyClearName = false; vm.EasyName = "名称\nshutdown"; vm.EasyNext.Execute(null); Check(vm.EasyFillVisible && vm.EasyHasError);
        });
        test("已有安全绑定的接入口可以同一关口换网络，保留全部绑定与策略", () =>
        {
            var d = Create(true); var state = db.GetState(d.Id)!; var r = BeginnerOperations.MoveNetwork(state, [P1], 20, false, true);
            var e = new ChangeEngine(db, drivers); var p = e.Preview(d, r); var c = p.Commands;
            int shut = Array.IndexOf(c, "shutdown"), remove = Array.IndexOf(c, "no switchport port-security mac-address 0211.2233.4455"), off = Array.IndexOf(c, "no switchport port-security"), vlan = Array.IndexOf(c, "switchport access vlan 20"), add = Array.IndexOf(c, "switchport port-security mac-address 0211.2233.4455"), up = Array.IndexOf(c, "no shutdown");
            Check(shut < remove && remove < off && off < vlan && vlan < add && add < up && c.Count(x => x == "shutdown") == 1 && c.Count(x => x == "no shutdown") == 1);
            e.ApplySimulation(p.Id, true); var after = db.GetState(d.Id)!; Check(after.MacBindings!.Single().VlanId == 20 && after.Ports[0].Pvid == 20 && after.PortSecurity!.Single() == state.PortSecurity!.Single());
            Check(after.Ports[1].Name == state.Ports[1].Name && after.Ports[1].Description == state.Ports[1].Description && after.Ports[1].Pvid == state.Ports[1].Pvid && after.Ports[1].Enabled == state.Ports[1].Enabled && after.Ports[1].TaggedVlans.SequenceEqual(state.Ports[1].TaggedVlans), "未选中的端口不应改变");
        });
        test("换网络保留原来关闭的端口，不借迁移顺便打开", () =>
        {
            var d = Create(true, true); var r = BeginnerOperations.MoveNetwork(db.GetState(d.Id)!, [P1], 20, false, true); var e = new ChangeEngine(db, drivers); var p = e.Preview(d, r);
            Check(!p.Commands.Contains("no shutdown")); e.ApplySimulation(p.Id, true); Check(!db.GetState(d.Id)!.Ports[0].Enabled && db.GetState(d.Id)!.Ports[0].Pvid == 20);
        });
        test("迁移拒绝漏绑、改地址、改安全策略以及未适配配置", () =>
        {
            var d = Create(true); var state = db.GetState(d.Id)!; var r = BeginnerOperations.MoveNetwork(state, [P1], 20, false, true); var e = new ChangeEngine(db, drivers);
            Reject(() => e.Preview(d, r with { RemoveMacBindings = [] })); Reject(() => e.Preview(d, r with { AddMacBindings = [r.AddMacBindings![0] with { Address = "02:11:22:33:44:66" }] }));
            Reject(() => e.Preview(d, r with { SecuritySettings = [new(P1, 1, "shutdown")] })); Reject(() => BeginnerOperations.MoveNetwork(state, [P1], 20, false, false));
            Reject(() => e.Preview(d, r with { SecuritySettings = [.. r.SecuritySettings!, new(P2, 1, "shutdown")] }));
            db.SaveState(state with { PortSecurity = [state.PortSecurity![0] with { HasUnsupportedOptions = true }] }); Reject(() => BeginnerOperations.MoveNetwork(db.GetState(d.Id)!, [P1], 20, false, true));
        });
        test("更换网络支持现有编号及同步创建，不用在不同页面往返", () =>
        {
            using var vm = Vm(Create(true)); Select(vm, EasyAction.PortNetwork); vm.EasyNetwork = vm.EasyNetworks.Single(n => n.Value == 20); vm.EasyNext.Execute(null);
            Check(vm.EasyReviewVisible && vm.PlanCommands.Contains("switchport access vlan 20") && !vm.EasyHasError);
            Check(!vm.CanOpenAdvancedTools && !vm.OpenDevice.CanExecute(null), "确认过程中不能切换目标或隐藏审核页");
            Check(vm.EasyReviewRows.Any(row => row.After.Contains("保留绑定")) && vm.EasyReviewRows.All(row => row.After != "删除"), "迁移审核应显示地址被保留，而非拆成删除和新增");
            vm.EasyBack.Execute(null); vm.EasyCreateNetwork = true; vm.EasyNetworkNumber = "50"; var r = vm.BuildEasyRequest(); Check(r.CreateVlanId == 50 && r.VlanName == "VLAN50" && r.MigratePortSecurity);
        });
        test("端口开关只改变开关，管理网络和原绑定保持原样", () =>
        {
            using var vm = Vm(Create(true)); Select(vm, EasyAction.PortPower); vm.EasyPower = vm.EasyPowerChoices.Single(p => !p.Value); var r = vm.BuildEasyRequest();
            Check(r.Enabled == false && r.Pvid is null && r.Mode is null && r.RemoveMacBindings is null); vm.EasyNext.Execute(null); Check(vm.EasyReviewVisible && vm.PlanCommands.Contains("shutdown") && !vm.PlanCommands.Contains("no switchport port-security"));
        });
        test("设备地址输入自动规范并合并重复，数量包含已有保留绑定", () =>
        {
            using var vm = Vm(Create(true)); Select(vm, EasyAction.AddMac); vm.EasyAddresses = "38-ff-59-06-1a-71\n38ff.5906.1a72\n38:FF:59:06:1A:71";
            var r = vm.BuildEasyRequest(); Check(r.AddMacBindings!.Length == 2 && r.RemoveMacBindings!.Length == 0 && r.SecuritySettings!.Single().Maximum == 3, "新增不能移除已存在的绑定");
            vm.EasyAutomaticMaximum = false; Check(vm.EasyMaximum == "3", "手动模式应接续刚才自动算出的数量"); vm.EasyAutomaticMaximum = true; vm.EasyNext.Execute(null);
            Check(vm.EasyReviewVisible && vm.EasyAddresses.Split(Environment.NewLine).Length == 2 && vm.PlanCommands.Contains("switchport port-security maximum 3"));
        });
        test("更换和取消设备绑定是独立操作，旧地址来自当前端口", () =>
        {
            var d = Create(true); using var vm = Vm(d); Select(vm, EasyAction.ReplaceMac); Check(vm.EasyOldMac is not null); vm.EasyAddresses = "38ff59061a71"; var r = vm.BuildEasyRequest();
            Check(r.RemoveMacBindings!.Single().Address == "02:11:22:33:44:55" && r.AddMacBindings!.Single().Address == "38:ff:59:06:1a:71");
            vm.ExitEasy.Execute(null); Select(vm, EasyAction.RemoveMac); r = vm.BuildEasyRequest(); Check(r.AddMacBindings is null && r.RemoveMacBindings!.Length == 1);
        });
        test("创建改名删除网络分别校验，默认和仍在使用的网络不能删除", () =>
        {
            using var vm = Vm(Create()); vm.StartEasy.Execute("CreateNetwork"); vm.EasyNetworkNumber = "60"; Check(vm.BuildEasyRequest().VlanName == "VLAN60"); vm.EasyNext.Execute(null); Check(vm.EasyReviewVisible); vm.ExitEasy.Execute(null);
            vm.StartEasy.Execute("RenameNetwork"); vm.EasyNetwork = vm.EasyNetworks.Single(v => v.Value == 20); vm.EasyNetworkName = "Office20"; vm.EasyNext.Execute(null); Check(vm.EasyReviewVisible); vm.ExitEasy.Execute(null);
            vm.StartEasy.Execute("DeleteNetwork"); vm.EasyNetwork = vm.EasyNetworks.Single(v => v.Value == 10); vm.EasyNext.Execute(null); Check(vm.EasyFillVisible && vm.EasyHasError && !vm.ReviewOpen);
            vm.EasyNetwork = vm.EasyNetworks.Single(v => v.Value == 1); vm.EasyNext.Execute(null); Check(vm.EasyFillVisible && vm.EasyHasError);
        });
        await asyncTest("普通操作不会执行或清掉其他草稿，成功后进入结果页", async () =>
        {
            var d = Create(true); using var vm = Vm(d); vm.Stage(new(CreateVlanId: 70, VlanName: "OtherDraft"), "另一个任务");
            Select(vm, EasyAction.PortName); vm.EasyName = "新手流程打印机"; vm.EasyNext.Execute(null); Check(!vm.PlanCommands.Contains("vlan 70") && vm.PendingChanges.Count == 1);
            vm.PlanConfirmed = true; vm.ApplyPlan.Execute(null); await Finish(vm);
            Check(vm.EasyResultVisible && !vm.EasyResultWarning && vm.EasyResult.Contains("模拟") && vm.PendingChanges.Count == 1 && !db.GetState(d.Id)!.Vlans.Any(v => v.Id == 70));
            Check(db.GetState(d.Id)!.Ports[0].Description == "新手流程打印机" && db.GetState(d.Id)!.MacBindings!.Single().VlanId == 10);
        });
        await asyncTest("备份有独立完成反馈，只新增快照不改变设备设置", async () =>
        {
            var d = Create(); using var vm = Vm(d); var before = ChangeEngine.ConfigurationHash(db.GetState(d.Id)!); vm.StartEasy.Execute("Backup"); vm.EasyNext.Execute(null); await Finish(vm);
            Check(vm.EasyResultVisible && vm.EasyResult.Contains("备份") && db.GetSnapshots(d.Id).Count == 1 && ChangeEngine.ConfigurationHash(db.GetState(d.Id)!) == before);
        });
    }
    private sealed class NoAi : IAiClient { public Task<string> AnalyzeAsync(AiProfile p, string q, string c, CancellationToken ct) => throw new NotSupportedException(); }
    private sealed class NoSessions : ISessionFactory { public IDeviceSession Create(ConnectionProtocol protocol) => throw new InvalidOperationException("本组检查不允许建立真实设备会话。"); }
}
